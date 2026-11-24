import { useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { findDuplicates, type DuplicateGroup } from './duplicates'
import type { Host } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

/**
 * 1.x's duplicates report (docs/architecture.md §7.46): hosts that look like the same machine, and combining them. One
 * is kept and gets what the others have that it lacks; the others are deleted.
 */
export function DuplicatesView({ onShowHost }: { onShowHost: (hostId: string) => void }) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const controller = new AbortController()
    api.get<Host[]>('/api/hosts', { signal: controller.signal }).then(
      (loaded) => {
        setHosts(loaded)
        setError(null)
      },
      (e: unknown) => {
        if (!controller.signal.aborted) {
          setError(e instanceof ApiError ? e.message : 'The hosts couldn’t be loaded.')
        }
      },
    )
    return () => controller.abort()
  }, [attempt])

  const groups = useMemo(() => findDuplicates(hosts ?? []), [hosts])

  if (error && !hosts) {
    return (
      <div className={styles.state}>
        <Alert>{error}</Alert>
        <Button variant="secondary" size="sm" onClick={() => setAttempt((a) => a + 1)}>
          Try again
        </Button>
      </div>
    )
  }
  if (!hosts) {
    return (
      <div className={styles.state}>
        <Spinner label="Loading hosts" />
      </div>
    )
  }
  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Duplicates</h2>
          <p className={styles.reportSummary}>
            {groups.length === 0 ? 'No hosts look like the same machine.' : `${groups.length} ${groups.length === 1 ? 'group' : 'groups'} of hosts that look like the same machine.`} Hosts
            match by MAC address, else IP address, else hostname, else name: hosts with different MAC addresses are never duplicates, and hosts reached through
            different jump hosts only match by MAC address. Combining keeps one host, gives it what the others have that it lacks, and deletes them.
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={() => setAttempt((a) => a + 1)}>
            Refresh
          </Button>
        </div>
      </header>
      {notice ? <Alert variant="info">{notice}</Alert> : null}
      {groups.map((group) => (
        <DuplicateCard
          key={group.id}
          group={group}
          onShowHost={onShowHost}
          onMerged={(kept, count) => {
            setNotice(`Combined ${count} ${count === 1 ? 'host' : 'hosts'} into ${kept.name}.`)
            setAttempt((a) => a + 1)
          }}
        />
      ))}
    </>
  )
}

function DuplicateCard({ group, onShowHost, onMerged }: { group: DuplicateGroup; onShowHost: (hostId: string) => void; onMerged: (kept: Host, count: number) => void }) {
  const [keepId, setKeepId] = useState(group.hosts[0]!.id)
  const [combine, setCombine] = useState<ReadonlySet<string>>(() => new Set(group.hosts.slice(1).map((h) => h.id)))
  const [busy, setBusy] = useState(false)
  const [asking, setAsking] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const keep = group.hosts.find((h) => h.id === keepId)!
  const others = group.hosts.filter((h) => h.id !== keepId && combine.has(h.id))
  const canCombine = others.length > 0 && keep.canManage && others.every((h) => h.canManage)

  function choose(id: string) {
    setKeepId(id)
    // The previously kept host joins the ones combined; the newly kept one leaves them.
    setCombine((current) => new Set([...current, keepId].filter((x) => x !== id)))
  }

  async function merge() {
    setBusy(true)
    setError(null)
    try {
      const kept = await api.post<Host>(`/api/hosts/${keep.id}/merge`, { sourceIds: others.map((h) => h.id) })
      onMerged(kept, others.length)
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The hosts couldn’t be combined.')
      setBusy(false)
      setAsking(false)
    }
  }

  return (
    <section className={styles.duplicateGroup} aria-label={`Duplicates of ${keep.name}`}>
      <p className={styles.duplicateReasons}>{group.reasons.join(' · ')}</p>
      {error ? <Alert>{error}</Alert> : null}
      <table className={styles.healthTable}>
        <thead>
          <tr>
            <th scope="col">Keep</th>
            <th scope="col">Combine</th>
            <th scope="col">Host</th>
            <th scope="col">Address</th>
            <th scope="col">MAC address</th>
            <th scope="col">Services</th>
            <th scope="col">Credentials</th>
          </tr>
        </thead>
        <tbody>
          {group.hosts.map((h) => (
            <tr key={h.id}>
              <td>
                <input type="radio" name={`keep-${group.id}`} aria-label={`Keep ${h.name}`} checked={h.id === keepId} onChange={() => choose(h.id)} />
              </td>
              <td>
                {h.id === keepId ? null : (
                  <input
                    type="checkbox"
                    aria-label={`Combine ${h.name}`}
                    checked={combine.has(h.id)}
                    onChange={(e) =>
                      setCombine((current) => {
                        const next = new Set(current)
                        if (e.target.checked) {
                          next.add(h.id)
                        } else {
                          next.delete(h.id)
                        }
                        return next
                      })
                    }
                  />
                )}
              </td>
              <td>
                <button type="button" className={styles.hostLink} onClick={() => onShowHost(h.id)}>
                  {h.name}
                </button>
              </td>
              <td className={styles.mono}>{h.hostname ?? h.ipAddress ?? ''}</td>
              <td className={styles.mono}>{h.macAddress ?? ''}</td>
              <td>{h.services.length}</td>
              <td>{h.credentials.length}</td>
            </tr>
          ))}
        </tbody>
      </table>
      <div className={styles.duplicateActions}>
        {asking ? (
          <Alert variant="warning">
            Combine {others.map((h) => h.name).join(', ')} into {keep.name}? They’re deleted, after {keep.name} gets what they have that it lacks.{' '}
            <Button size="sm" variant="danger" loading={busy} onClick={() => void merge()}>
              Combine and delete {others.length === 1 ? 'it' : 'them'}
            </Button>{' '}
            <Button size="sm" variant="ghost" onClick={() => setAsking(false)}>
              Cancel
            </Button>
          </Alert>
        ) : (
          <Button size="sm" disabled={!canCombine} onClick={() => setAsking(true)} title={canCombine || others.length === 0 ? undefined : 'You need Manage on every one of these hosts.'}>
            Combine {others.length} into {keep.name}
          </Button>
        )}
      </div>
    </section>
  )
}
