import { useEffect, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Catalog } from './catalog'
import { CERTIFICATE_WARNING_DAYS, daysUntil, expiryText } from './health'
import { hostAddress } from './hosts'
import type { Host, HostService, ServiceHealth } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import styles from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

const DOWN_REASONS: Record<string, string> = { refused: 'Connection refused', timeout: 'Timed out', unreachable: 'Unreachable' }
const timeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })
const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })

interface Row {
  host: Host
  service: HostService
  check: ServiceHealth
}

/**
 * The health of every service the user can see (docs/architecture.md §7.13, §7.14): what's down, and which
 * certificates have expired or soon will. Ported from 1.x's health report, and drawn as it was.
 */
export function HealthView({ catalog, onShowHost }: { catalog: Catalog; onShowHost: (hostId: string) => void }) {
  const [data, setData] = useState<{ hosts: Host[]; health: ServiceHealth[] } | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [reloads, setReloads] = useState(0)

  useEffect(() => {
    let cancelled = false
    Promise.all([api.get<Host[]>('/api/hosts'), api.get<ServiceHealth[]>('/api/health')]).then(
      ([hosts, health]) => {
        if (!cancelled) {
          setData({ hosts, health })
          setError(null)
          setLoading(false)
        }
      },
      (failure: unknown) => {
        if (!cancelled) {
          setError(failure instanceof ApiError ? failure.message : 'The health results could not be loaded.')
          setLoading(false)
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [reloads])

  if (!data) {
    return <div className={styles.state}>{error ? <Alert>{error}</Alert> : <Spinner label="Loading health" />}</div>
  }

  const byService = new Map(data.health.map((c) => [c.serviceId, c]))
  const rows: Row[] = data.hosts.flatMap((host) =>
    host.services.flatMap((service) => {
      const check = byService.get(service.id)
      return check ? [{ host, service, check }] : []
    }),
  )
  const down = rows.filter((r) => r.check.state === 'down').sort((a, b) => a.check.checkedAtUtc.localeCompare(b.check.checkedAtUtc))
  const certificates = rows
    .filter((r): r is Row & { check: { certificateExpiresAtUtc: string } } => !!r.check.certificateExpiresAtUtc)
    .sort((a, b) => a.check.certificateExpiresAtUtc.localeCompare(b.check.certificateExpiresAtUtc))
  const expiring = certificates.filter((r) => daysUntil(r.check.certificateExpiresAtUtc) <= CERTIFICATE_WARNING_DAYS).length
  const unchecked = data.hosts.filter((h) => h.services.length > 0 && !h.services.some((s) => byService.has(s.id))).length
  const latest = rows.reduce<string | null>((last, r) => (last === null || r.check.checkedAtUtc > last ? r.check.checkedAtUtc : last), null)

  /** "SSH:22", as 1.x named a service in its reports. */
  const serviceName = (row: Row) => {
    const type = catalog.serviceTypes.get(row.service.type)
    const port = row.service.port ?? type?.defaultPort
    return `${row.service.label ?? type?.label ?? row.service.type}${row.service.address ? ` ${row.service.address}` : ''}${port ? `:${port}` : ''}`
  }
  const hostLink = (host: Host) => (
    <button type="button" className={styles.hostLink} onClick={() => onShowHost(host.id)} title={hostAddress(host) ?? undefined}>
      {host.name}
    </button>
  )

  return (
    <>
      <header className={styles.reportHeader}>
        <div>
          <h2 className={styles.reportTitle}>Health</h2>
          <p className={styles.reportSummary}>
            {rows.length - down.length} of {rows.length} services up · {down.length} down · {expiring} {expiring === 1 ? 'certificate' : 'certificates'} expiring
            within {CERTIFICATE_WARNING_DAYS} days
            {unchecked > 0 ? ` · ${unchecked} ${unchecked === 1 ? 'host' : 'hosts'} not checked yet` : ''}
            {latest ? ` · last check ${timeFormat.format(new Date(latest))}` : ''}
          </p>
        </div>
        <div className={styles.reportActions}>
          <Button
            size="sm"
            variant="secondary"
            onClick={() => {
              setLoading(true)
              setReloads((n) => n + 1)
            }}
            disabled={loading}
          >
            Refresh
          </Button>
        </div>
      </header>
      {error && <Alert>{error}</Alert>}

      <div className={styles.tableScroll}>
        <section className={styles.healthSection} aria-labelledby="health-down">
          <h3 id="health-down" className={styles.healthTitle}>
            Down
          </h3>
          {down.length === 0 ? (
            <p className={styles.healthEmpty}>Every checked service is answering.</p>
          ) : (
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">Host</th>
                  <th scope="col">Service</th>
                  <th scope="col">Checked</th>
                  <th scope="col">Why</th>
                </tr>
              </thead>
              <tbody>
                {down.map((row) => (
                  <tr key={row.service.id}>
                    <td>{hostLink(row.host)}</td>
                    <td className={styles.mono}>{serviceName(row)}</td>
                    <td>{timeFormat.format(new Date(row.check.checkedAtUtc))}</td>
                    <td className={styles.overcommitted}>{row.check.reason ? (DOWN_REASONS[row.check.reason] ?? row.check.reason) : 'Down'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </section>

        <section className={styles.healthSection} aria-labelledby="health-certificates">
          <h3 id="health-certificates" className={styles.healthTitle}>
            Certificates
          </h3>
          {certificates.length === 0 ? (
            <p className={styles.healthEmpty}>No certificates have been read yet.</p>
          ) : (
            <table className={styles.healthTable}>
              <thead>
                <tr>
                  <th scope="col">Host</th>
                  <th scope="col">Service</th>
                  <th scope="col">Issued to</th>
                  <th scope="col">Expires</th>
                </tr>
              </thead>
              <tbody>
                {certificates.map((row) => {
                  const expires = row.check.certificateExpiresAtUtc
                  return (
                    <tr key={row.service.id}>
                      <td>{hostLink(row.host)}</td>
                      <td className={styles.mono}>{serviceName(row)}</td>
                      <td>{row.check.certificateSubject ?? '—'}</td>
                      <td className={daysUntil(expires) <= CERTIFICATE_WARNING_DAYS ? styles.overcommitted : undefined} data-warning={daysUntil(expires) <= CERTIFICATE_WARNING_DAYS || undefined}>
                        {dateFormat.format(new Date(expires))} ({expiryText(expires)})
                      </td>
                    </tr>
                  )
                })}
              </tbody>
            </table>
          )}
        </section>
      </div>
    </>
  )
}
