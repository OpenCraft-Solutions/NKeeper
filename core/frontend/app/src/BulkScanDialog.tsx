import { useState } from 'react'
import { api, ApiError, type TrustPrompt } from '@nkeeper/sdk'
import { connectMenu, type Catalog, type ConnectEntry, type ConnectGroup } from './catalog'
import { useDialogs } from './dialogContext'
import { draftOf, requestOf } from './hostDraft'
import { scanProposals, type ScanProposal } from './scanProposals'
import type { Host, ScanFindings } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { Checkbox } from './ui/Checkbox'
import { Dialog } from './ui/Dialog'
import styles from './ui/ScanDialog.module.css'
import { Spinner } from './ui/Spinner'

/** How a host is scanned: its first scanner that signs in with a saved credential, or a host-wide one (1.x). */
interface Plan {
  host: Host
  scan: { group: ConnectGroup; entry: ConnectEntry } | null
}

interface Result {
  host: Host
  proposals: ScanProposal[]
  problem: string | null
}

type Step = { kind: 'choose' } | { kind: 'running'; done: number } | { kind: 'results'; results: Result[]; picked: Set<string> } | { kind: 'applying' }

/** A proposal's key across hosts. */
const keyOf = (host: Host, proposal: ScanProposal) => `${host.id}/${proposal.key}`

function planScan(host: Host, catalog: Catalog): Plan {
  for (const group of connectMenu(host, catalog, true)) {
    for (const entry of group.entries) {
      if (entry.target.kind === 'run' && (entry.credential.kind === 'saved' || entry.credential.kind === 'none')) {
        return { host, scan: { group, entry } }
      }
    }
  }
  return { host, scan: null }
}

/**
 * 1.x's bulk scan (docs/architecture.md §7.50): each picked host scanned with its saved login, one after another, then
 * what each scan found as proposed changes to that host, applied together as ordinary host edits. A server key that
 * isn't trusted yet is asked about, when asked to, and otherwise skips the host: a bulk scan never trusts one by itself.
 */
export function BulkScanDialog({
  hosts,
  allHosts,
  catalog,
  onClose,
  onApplied,
}: {
  hosts: Host[]
  allHosts: Host[]
  catalog: Catalog
  onClose: () => void
  onApplied: (updated: Host[]) => void
}) {
  const dialogs = useDialogs()
  const plans = hosts.filter((h) => h.canManage).map((h) => planScan(h, catalog))
  const scannable = plans.filter((p) => p.scan)
  const [askKeys, setAskKeys] = useState(false)
  const [step, setStep] = useState<Step>({ kind: 'choose' })
  const [error, setError] = useState<string | null>(null)

  async function trust(prompt: TrustPrompt, host: Host) {
    const name = prompt.hostId === host.id ? host.name : (allHosts.find((h) => h.id === prompt.hostId)?.name ?? `the jump host of ${host.name}`)
    if (!(await dialogs.confirmHostKey(prompt, name))) {
      return false
    }
    await api.post(`/api/hosts/${prompt.hostId}/services/${prompt.serviceId}/trusted-keys`, { algorithm: prompt.algorithm, fingerprint: prompt.fingerprint })
    return true
  }

  async function scanOne({ host, scan }: Plan): Promise<Result> {
    const { entry } = scan!
    if (entry.target.kind !== 'run') {
      return { host, proposals: [], problem: 'Nothing scans it.' }
    }
    const body: Record<string, unknown> = entry.serviceId ? { serviceId: entry.serviceId } : {}
    if (entry.credential.kind === 'saved') {
      body.credentialId = entry.credential.id
    }
    const url = `/api/p/${encodeURIComponent(entry.target.pluginId)}/hosts/${host.id}/${entry.target.endpoint}`
    for (let attempt = 0; ; attempt++) {
      try {
        const findings = (await api.post<ScanFindings>(url, body)) ?? {}
        // Hosts for neighbours a scan saw are added one host's scan at a time (§7.26).
        const proposals = scanProposals(host, findings, catalog.serviceTypes, allHosts, false).filter((p) => p.apply)
        return { host, proposals, problem: null }
      } catch (failure) {
        const presented = failure instanceof ApiError && failure.code === 'host_key_unknown' ? (failure.problem as { presented?: TrustPrompt } | undefined)?.presented : undefined
        if (presented && attempt === 0 && askKeys && (await trust(presented, host))) {
          continue
        }
        const problem = presented ? 'Its server key isn’t trusted yet: skipped.' : failure instanceof ApiError ? failure.message : 'The scan failed.'
        return { host, proposals: [], problem }
      }
    }
  }

  async function run() {
    setError(null)
    const results: Result[] = []
    for (const plan of scannable) {
      setStep({ kind: 'running', done: results.length })
      results.push(await scanOne(plan))
    }
    setStep({ kind: 'results', results, picked: new Set(results.flatMap((r) => r.proposals.filter((p) => p.checked).map((p) => keyOf(r.host, p)))) })
  }

  async function apply(state: Extract<Step, { kind: 'results' }>) {
    setError(null)
    setStep({ kind: 'applying' })
    const updated: Host[] = []
    const failed: string[] = []
    for (const { host, proposals } of state.results) {
      const chosen = proposals.filter((p) => state.picked.has(keyOf(host, p)))
      if (chosen.length === 0) {
        continue
      }
      // From the version the scan started with, so an edit made since is a conflict rather than lost.
      const draft = draftOf(host)
      chosen.forEach((p) => p.apply!(draft))
      try {
        updated.push(await api.put<Host>(`/api/hosts/${host.id}`, requestOf(draft, host.version)))
      } catch (failure) {
        const conflict = failure instanceof ApiError && failure.status === 409
        failed.push(`${host.name}: ${conflict ? 'someone changed it since the scan started' : failure instanceof ApiError ? failure.message : 'it couldn’t be saved'}`)
      }
    }
    onApplied(updated)
    if (failed.length === 0) {
      onClose()
      return
    }
    setError(`Saved ${updated.length}; not saved: ${failed.join('; ')}.`)
    setStep(state)
  }

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
        <Button variant="secondary" onClick={onClose} disabled={step.kind !== 'choose'}>
          Cancel
        </Button>
        <Button onClick={() => void run()} disabled={scannable.length === 0 || step.kind !== 'choose'} loading={step.kind === 'running' || step.kind === 'applying'}>
          Scan {scannable.length === 1 ? '1 host' : `${scannable.length} hosts`}
        </Button>
      </>
    )

  return (
    <Dialog
      title={`Scan ${hosts.length} hosts`}
      description={step.kind === 'results' ? 'Choose what to apply. Nothing is saved until you do.' : 'Each host is scanned with its saved login. Nothing is saved until you review the results.'}
      onClose={onClose}
      footer={footer}
      size="lg"
    >
      <div className={styles.form}>
        {error && <Alert>{error}</Alert>}
        {step.kind === 'running' ? (
          <p className={styles.running} role="status">
            <Spinner size="sm" /> Scanned {step.done} of {scannable.length}…
          </p>
        ) : step.kind === 'applying' ? (
          <p className={styles.running} role="status">
            <Spinner size="sm" /> Saving…
          </p>
        ) : step.kind === 'results' ? (
          <Results step={step} onPick={(picked) => setStep({ ...step, picked })} />
        ) : (
          <>
            <ul className={styles.notes} aria-label="Hosts to scan">
              {plans.map(({ host, scan }) => (
                <li key={host.id}>
                  {host.name}: {scan ? `${scan.group.label} · ${scan.entry.label}` : 'no scanner with a saved login, so skipped'}
                </li>
              ))}
              {hosts.length > plans.length && <li>{hosts.length - plans.length} you can’t change, so skipped</li>}
            </ul>
            <Checkbox label="Ask me about server keys not trusted yet (otherwise those hosts are skipped)" checked={askKeys} onChange={(e) => setAskKeys(e.target.checked)} />
          </>
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
      {step.results.map(({ host, proposals, problem }) => (
        <section key={host.id} className={styles.group} aria-label={host.name}>
          <h3 className={styles.groupTitle}>{host.name}</h3>
          {problem ? <p className={styles.hint}>{problem}</p> : proposals.length === 0 ? <p className={styles.hint}>Nothing it doesn’t already have.</p> : null}
          {proposals.map((p) => (
            <label key={p.key} className={styles.proposal}>
              <input type="checkbox" checked={step.picked.has(keyOf(host, p))} onChange={(e) => toggle(keyOf(host, p), e.target.checked)} />
              <span>{p.label}</span>
              <span className={styles.detail}>{p.detail}</span>
            </label>
          ))}
        </section>
      ))}
    </>
  )
}
