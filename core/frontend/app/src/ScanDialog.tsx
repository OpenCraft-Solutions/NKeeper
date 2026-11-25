import { useState } from 'react'
import { api, ApiError, type TrustPrompt } from '@nkeeper/sdk'
import { connectMenu, type Catalog, type ConnectEntry, type ConnectGroup } from './catalog'
import { useDialogs } from './dialogContext'
import { draftOf, requestOf } from './hostDraft'
import { scanProposals, type ScanProposal } from './scanProposals'
import type { Host, ScanFindings } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { Dialog } from './ui/Dialog'
import styles from './ui/ScanDialog.module.css'
import { Spinner } from './ui/Spinner'

type Step = { kind: 'choose' } | { kind: 'running' } | { kind: 'results'; proposals: ScanProposal[]; picked: Set<string>; notes: string[] } | { kind: 'applying' }

const GROUPS: ScanProposal['group'][] = ['Host', 'Services', 'Logins', 'Virtual machines', 'New hosts']

/**
 * Tasks ▸ Scan (docs/architecture.md §7.26): runs a plugin's scan on one of the host's services, then shows what it
 * found as proposed changes. Nothing is saved until the user applies the ones they pick, with ordinary host edits.
 */
export function ScanDialog({
  host,
  hosts,
  catalog,
  canCreateHosts,
  onClose,
  onApplied,
}: {
  host: Host
  hosts: Host[]
  catalog: Catalog
  canCreateHosts: boolean
  onClose: () => void
  /** What was saved: the host, if it changed, the hosts added, and other hosts changed (its guests, §7.49). Called before closing, and after a partial failure. */
  onApplied: (updated: Host | null, created: Host[], others?: Host[]) => void
}) {
  const dialogs = useDialogs()
  const choices = connectMenu(host, catalog, true).flatMap((group) => group.entries.map((entry) => ({ group, entry })))
  const [choice, setChoice] = useState(choices[0]?.entry.key ?? '')
  const [step, setStep] = useState<Step>({ kind: 'choose' })
  const [error, setError] = useState<string | null>(null)

  async function scan(group: ConnectGroup, entry: ConnectEntry) {
    if (entry.target.kind !== 'run') {
      return
    }
    // A host-wide scanner takes no service (SDK 1.14).
    const body: Record<string, unknown> = entry.serviceId ? { serviceId: entry.serviceId } : {}
    if (entry.credential.kind === 'saved') {
      body.credentialId = entry.credential.id
    }
    const url = `/api/p/${encodeURIComponent(entry.target.pluginId)}/hosts/${host.id}/${entry.target.endpoint}`
    setError(null)
    setStep({ kind: 'running' })
    try {
      // A typed credential is used up by one try, so it's asked for again after trusting a key.
      for (let attempt = 0; ; attempt++) {
        if (entry.credential.kind === 'typed') {
          const typed = await dialogs.askCredential(host.name, group.label)
          if (!typed) {
            setStep({ kind: 'choose' })
            return
          }
          body.promptReference = (await api.post<{ reference: string }>(`/api/hosts/${host.id}/services/${entry.serviceId}/credential-prompt`, typed)).reference
        }
        try {
          const findings = await api.post<ScanFindings>(url, body)
          const proposals = scanProposals(host, findings ?? {}, catalog.serviceTypes, hosts, canCreateHosts)
          setStep({ kind: 'results', proposals, picked: new Set(proposals.filter((p) => p.checked).map((p) => p.key)), notes: findings?.notes ?? [] })
          return
        } catch (failure) {
          const presented = failure instanceof ApiError && failure.code === 'host_key_unknown' ? (failure.problem as { presented?: TrustPrompt } | undefined)?.presented : undefined
          if (!presented || attempt > 0 || !(await trust(presented))) {
            throw failure
          }
        }
      }
    } catch (failure) {
      setError(failure instanceof ApiError ? failure.message : 'The scan failed.')
      setStep({ kind: 'choose' })
    }
  }

  // Scanning needs Manage, which is also what trusting a key for the host needs (§7.7).
  async function trust(prompt: TrustPrompt) {
    const name = prompt.hostId === host.id ? host.name : (hosts.find((h) => h.id === prompt.hostId)?.name ?? `the jump host of ${host.name}`)
    if (!(await dialogs.confirmHostKey(prompt, name))) {
      return false
    }
    await api.post(`/api/hosts/${prompt.hostId}/services/${prompt.serviceId}/trusted-keys`, { algorithm: prompt.algorithm, fingerprint: prompt.fingerprint })
    return true
  }

  async function apply(results: Extract<Step, { kind: 'results' }>) {
    const { proposals, picked } = results
    const chosen = proposals.filter((p) => picked.has(p.key))
    const edits = chosen.filter((p) => p.apply)
    setError(null)
    setStep({ kind: 'applying' })
    let updated: Host | null = null
    const created: Host[] = []
    const others: Host[] = []
    try {
      if (edits.length > 0) {
        // From the version the dialog opened with, so an edit made since is a conflict rather than lost.
        const draft = draftOf(host)
        edits.forEach((p) => p.apply!(draft))
        updated = await api.put<Host>(`/api/hosts/${host.id}`, requestOf(draft, host.version))
      }
      for (const p of chosen) {
        if (p.newHost) {
          created.push(await api.post<Host>('/api/hosts', { ...p.newHost, tags: [], services: [], credentials: [] }))
        }
      }
      // Each guest found is an edit of its own host, from the version the dialog opened with.
      for (const p of chosen) {
        if (p.other) {
          const draft = draftOf(p.other.host)
          p.other.apply(draft)
          others.push(await api.put<Host>(`/api/hosts/${p.other.host.id}`, requestOf(draft, p.other.host.version)))
        }
      }
      onApplied(updated, created, others)
      onClose()
    } catch (failure) {
      if (updated || created.length > 0 || others.length > 0) {
        onApplied(updated, created, others)
      }
      const conflict = failure instanceof ApiError && failure.status === 409 && failure.code === 'version_conflict'
      setError(conflict ? 'Someone else changed this host since the scan started. Close this, and scan again.' : failure instanceof ApiError ? failure.message : 'The changes could not be saved.')
      setStep(results)
    }
  }

  const selected = choices.find((c) => c.entry.key === choice)
  const footer =
    step.kind === 'results' ? (
      <>
        <Button variant="secondary" onClick={onClose}>
          Close
        </Button>
        <Button onClick={() => void apply(step)} disabled={step.picked.size === 0}>
          Apply
        </Button>
      </>
    ) : (
      <>
        <Button variant="secondary" onClick={onClose}>
          Cancel
        </Button>
        <Button onClick={() => selected && void scan(selected.group, selected.entry)} disabled={!selected || step.kind !== 'choose'} loading={step.kind === 'running' || step.kind === 'applying'}>
          Scan
        </Button>
      </>
    )

  return (
    <Dialog
      title={`Scan ${host.name}`}
      description={step.kind === 'results' ? 'Choose what to apply. Nothing is saved until you do.' : 'One login that reads the host’s own settings. Nothing is saved until you review the results.'}
      onClose={onClose}
      footer={footer}
      size="lg"
    >
      <div className={styles.form}>
        {error && <Alert>{error}</Alert>}
        {step.kind === 'results' ? (
          <Results step={step} onPick={(picked) => setStep({ ...step, picked })} />
        ) : step.kind === 'running' ? (
          <p className={styles.running} role="status">
            <Spinner size="sm" /> Scanning…
          </p>
        ) : step.kind === 'applying' ? (
          <p className={styles.running} role="status">
            <Spinner size="sm" /> Saving…
          </p>
        ) : choices.length === 0 ? (
          <p className={styles.hint}>No loaded plugin scans this host’s services.</p>
        ) : (
          <fieldset className={styles.choices}>
            <legend className={styles.groupTitle}>Scan with</legend>
            {choices.map(({ group, entry }) => (
              <label key={entry.key} className={styles.choice}>
                <input type="radio" name="scan-choice" value={entry.key} checked={choice === entry.key} onChange={() => setChoice(entry.key)} />
                {group.label} · {entry.label}
              </label>
            ))}
          </fieldset>
        )}
      </div>
    </Dialog>
  )
}

function Results({ step, onPick }: { step: Extract<Step, { kind: 'results' }>; onPick: (picked: Set<string>) => void }) {
  const toggle = (key: string, on: boolean) => {
    const picked = new Set(step.picked)
    if (on) {
      picked.add(key)
    } else {
      picked.delete(key)
    }
    onPick(picked)
  }
  return (
    <>
      {step.proposals.length === 0 && <p className={styles.hint}>The scan found nothing this host doesn’t already have.</p>}
      {GROUPS.map((name) => {
        const proposals = step.proposals.filter((p) => p.group === name)
        return (
          proposals.length > 0 && (
            <section key={name} className={styles.group} aria-label={name}>
              <h3 className={styles.groupTitle}>{name}</h3>
              {proposals.map((p) => (
                <label key={p.key} className={styles.proposal}>
                  <input type="checkbox" checked={step.picked.has(p.key)} onChange={(e) => toggle(p.key, e.target.checked)} />
                  <span>{p.label}</span>
                  <span className={styles.detail}>{p.detail}</span>
                </label>
              ))}
            </section>
          )
        )
      })}
      {step.notes.length > 0 && (
        <section className={styles.group} aria-label="Notes">
          <h3 className={styles.groupTitle}>Notes</h3>
          <ul className={styles.notes}>
            {step.notes.map((note, i) => (
              <li key={i}>{note}</li>
            ))}
          </ul>
        </section>
      )}
    </>
  )
}
