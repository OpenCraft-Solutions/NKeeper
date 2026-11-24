import { useCallback, useEffect, useMemo, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { formatMemory } from './scanProposals'
import type { Host } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'
import { buildVmResources, share, vmResourcesCsv, type HypervisorGroup } from './vmResources'

const COLUMNS = 6

function downloadCsv(name: string, text: string) {
  const url = URL.createObjectURL(new Blob([text], { type: 'text/csv;charset=utf-8' }))
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  URL.revokeObjectURL(url)
}

/**
 * 1.x's VM hosts & resources report (docs/architecture.md §7.49): each VM host with its CPU cores, memory and serial number, and what its VMs are given in total;
 * below it, each VM with its vCPUs and memory and its share of the host. Then physical servers that
 * run no VMs (with their serial numbers), and VMs whose host isn't known. Shares over 100% mean the
 * host is overcommitted.
 */
export function VmResourcesView({ onShowHost }: { onShowHost: (hostId: string) => void }) {
  const hosts = useHosts()
  const report = useMemo(() => buildVmResources(hosts.data ?? []), [hosts.data])
  const vmCount = report.hypervisors.reduce((sum, group) => sum + group.guests.length, 0) + report.unplaced.length

  if (hosts.error && !hosts.data) {
    return (
      <div className={styles.state}>
        <Alert>{hosts.error}</Alert>
        <Button variant="secondary" size="sm" onClick={hosts.reload}>
          Try again
        </Button>
      </div>
    )
  }

  if (!hosts.data) {
    return (
      <div className={styles.state}>
        <Spinner label="Loading hosts" />
      </div>
    )
  }

  const empty = report.hypervisors.length === 0 && report.physical.length === 0 && report.unplaced.length === 0

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>VM hosts & resources</h2>
          <p className={styles.reportSummary}>
            {plural(report.hypervisors.length, 'VM host')} · {plural(vmCount, 'VM')} · {plural(report.physical.length, 'other physical server')}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button size="sm" variant="secondary" onClick={hosts.reload} disabled={hosts.loading}>
            Refresh
          </Button>
          <Button size="sm" onClick={() => downloadCsv('nkeeper-vm-resources.csv', vmResourcesCsv(report))} disabled={empty}>
            Download CSV
          </Button>
        </div>
      </header>

      {empty ? (
        <p className={styles.state}>No hosts yet.</p>
      ) : (
        <div className={styles.tableScroll}>
          <table className={`${styles.grid} ${styles.resources}`}>
            <thead>
              <tr>
                <th scope="col" className={styles.corner}>
                  Host
                </th>
                <th scope="col">Serial number</th>
                <th scope="col">CPU</th>
                <th scope="col">vCPUs allocated</th>
                <th scope="col">Memory</th>
                <th scope="col">Memory allocated</th>
              </tr>
            </thead>
            {report.hypervisors.map((group) => (
              <HypervisorRows key={group.host.id} group={group} onShowHost={onShowHost} />
            ))}
            {report.physical.length > 0 ? (
              <tbody>
                <SectionRow title="Physical servers without VMs" />
                {report.physical.map((host) => (
                  <tr key={host.id}>
                    <th scope="row">
                      <HostLink host={host} onShowHost={onShowHost} />
                    </th>
                    <td className={styles.mono}>{host.serialNumber ?? ''}</td>
                    <td>{cores(host.cpuCores, 'core')}</td>
                    <td />
                    <td>{host.memoryMiB ? formatMemory(host.memoryMiB) : ''}</td>
                    <td />
                  </tr>
                ))}
              </tbody>
            ) : null}
            {report.unplaced.length > 0 ? (
              <tbody>
                <SectionRow title="VMs whose host isn’t known" />
                {report.unplaced.map((host) => (
                  <tr key={host.id}>
                    <th scope="row">
                      <HostLink host={host} onShowHost={onShowHost} />
                    </th>
                    <td />
                    <td>{cores(host.cpuCores, 'vCPU')}</td>
                    <td />
                    <td>{host.memoryMiB ? formatMemory(host.memoryMiB) : ''}</td>
                    <td />
                  </tr>
                ))}
              </tbody>
            ) : null}
          </table>
        </div>
      )}
    </>
  )
}

/** A VM host's row (its capacity and what its VMs are given), then one row per VM with its share. */
function HypervisorRows({ group, onShowHost }: { group: HypervisorGroup; onShowHost: (hostId: string) => void }) {
  const { host, guests, allocatedCpus, allocatedMemoryMiB, incomplete } = group
  const missing = incomplete > 0 ? `${plural(incomplete, 'VM')} with unknown resources not counted` : undefined
  return (
    <tbody>
      <tr className={styles.hypervisorRow}>
        <th scope="row">
          <HostLink host={host} onShowHost={onShowHost} />
          <span className={styles.rowNote}>{plural(guests.length, 'VM')}</span>
        </th>
        <td className={styles.mono}>{host.isVirtualMachine ? '' : (host.serialNumber ?? '')}</td>
        <td>{cores(host.cpuCores, host.isVirtualMachine ? 'vCPU' : 'core')}</td>
        <td title={missing}>
          {allocatedCpus > 0 ? <Usage text={String(allocatedCpus)} percent={share(allocatedCpus, host.cpuCores)} /> : ''}
          {missing ? <span className={styles.rowNote}>+{incomplete} unknown</span> : null}
        </td>
        <td>{host.memoryMiB ? formatMemory(host.memoryMiB) : ''}</td>
        <td title={missing}>
          {allocatedMemoryMiB > 0 ? <Usage text={formatMemory(allocatedMemoryMiB)} percent={share(allocatedMemoryMiB, host.memoryMiB)} /> : ''}
        </td>
      </tr>
      {guests.map((guest) => (
        <tr key={guest.id} className={styles.guestRow}>
          <th scope="row">
            <HostLink host={guest} onShowHost={onShowHost} />
          </th>
          <td />
          <td>{cores(guest.cpuCores, 'vCPU')}</td>
          <td>
            <Usage percent={share(guest.cpuCores, host.cpuCores)} suffix=" of host" />
          </td>
          <td>{guest.memoryMiB ? formatMemory(guest.memoryMiB) : ''}</td>
          <td>
            <Usage percent={share(guest.memoryMiB, host.memoryMiB)} suffix=" of host" />
          </td>
        </tr>
      ))}
    </tbody>
  )
}

/** "24 (75%)", flagged when over 100% (the host is overcommitted). */
function Usage({ text, percent, suffix = '' }: { text?: string; percent: number | null; suffix?: string }) {
  if (percent === null) {
    return text ? <>{text}</> : null
  }
  const label = `${percent}%${suffix}`
  return (
    <span className={percent > 100 ? styles.overcommitted : undefined} title={percent > 100 ? 'More than the host has (overcommitted)' : undefined}>
      {text ? `${text} (${label})` : label}
    </span>
  )
}

function SectionRow({ title }: { title: string }) {
  return (
    <tr className={styles.sectionRow}>
      <th scope="colgroup" colSpan={COLUMNS}>
        {title}
      </th>
    </tr>
  )
}

function HostLink({ host, onShowHost }: { host: Host; onShowHost: (hostId: string) => void }) {
  return (
    <button type="button" className={styles.hostLink} onClick={() => onShowHost(host.id)}>
      {host.name}
    </button>
  )
}

/** The hosts the user can see, reloadable. */
function useHosts() {
  const [data, setData] = useState<Host[] | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [generation, setGeneration] = useState(0)
  useEffect(() => {
    const controller = new AbortController()
    api.get<Host[]>('/api/hosts', { signal: controller.signal }).then(
      (hosts) => {
        setData(hosts)
        setError(null)
        setLoading(false)
      },
      (e: unknown) => {
        if (!controller.signal.aborted) {
          setError(e instanceof ApiError ? e.message : 'The hosts couldn’t be loaded.')
          setLoading(false)
        }
      },
    )
    return () => controller.abort()
  }, [generation])
  const reload = useCallback(() => {
    setLoading(true)
    setGeneration((g) => g + 1)
  }, [])
  return { data, error, loading, reload }
}

function cores(count: number | null, unit: string): string {
  return count ? `${count} ${unit}${count === 1 ? '' : 's'}` : ''
}

function plural(count: number, noun: string): string {
  return `${count} ${noun}${count === 1 ? '' : 's'}`
}
