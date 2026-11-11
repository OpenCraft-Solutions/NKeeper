import { useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Catalog } from './catalog'
import { CERTIFICATE_WARNING_DAYS, daysUntil } from './health'
import { credentialName, usedWith } from './hosts'
import type { Host, ServiceHealth } from './types'
import { Alert } from './ui/Alert'
import { Badge } from './ui/Badge'
import { Button } from './ui/Button'
import { DeleteButton } from './ui/DeleteButton'
import styles from './ui/HostPanel.module.css'

const DOWN_REASONS: Record<string, string> = { refused: 'Connection refused', timeout: 'Timed out', unreachable: 'Unreachable' }
const dateTimeFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium', timeStyle: 'short' })

/**
 * A host's details (docs/architecture.md §7.14), as 1.x's host panel drew them: its Connect, Edit and Delete,
 * overview, tags, services with their health, credentials, and the server keys it trusts.
 */
export function HostDetails({
  host,
  hosts,
  catalog,
  health,
  onEdit,
  onDeleted,
  onKeyRemoved,
  onCheck,
  connectable,
  onConnectMenu,
}: {
  host: Host
  hosts: Host[]
  catalog: Catalog
  health: Map<string, ServiceHealth>
  onEdit: () => void
  onDeleted: () => void
  /** A trusted key was removed: applied to the latest host, so two removals at once both stay removed. */
  onKeyRemoved: (keyId: string) => void
  onCheck: () => Promise<void>
  /** Whether any loaded plugin opens something on the host's services. */
  connectable: boolean
  /** Opens the Connect menu under the button. */
  onConnectMenu: (anchor: DOMRect) => void
}) {
  const [error, setError] = useState<string | null>(null)
  const [deleting, setDeleting] = useState(false)
  const [checking, setChecking] = useState(false)
  const jumpHost = host.jump ? hosts.find((h) => h.id === host.jump!.hostId) : undefined
  const addresses = [host.hostname, host.ipAddress].filter(Boolean).join(' · ')

  async function remove() {
    setError(null)
    setDeleting(true)
    try {
      await api.delete(`/api/hosts/${host.id}`)
      onDeleted()
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'The host could not be deleted.')
      setDeleting(false)
    }
  }

  async function removeKey(keyId: string) {
    setError(null)
    try {
      await api.delete(`/api/hosts/${host.id}/trusted-keys/${keyId}`)
      onKeyRemoved(keyId)
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'The key could not be removed.')
    }
  }

  async function check() {
    setChecking(true)
    setError(null)
    try {
      await onCheck()
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'The host could not be checked.')
    } finally {
      setChecking(false)
    }
  }

  return (
    <article className={styles.panel} aria-labelledby="host-title" aria-label={host.name}>
      <header className={styles.header}>
        <div className={styles.heading}>
          <h1 id="host-title" className={styles.title}>
            {host.name}
          </h1>
          <p className={`${styles.subtitle} ${styles.mono}`}>{addresses}</p>
        </div>
        <div className={styles.actions}>
          {host.canManage && (
            <>
              <DeleteButton question="Delete this host?" onConfirm={() => void remove()} loading={deleting} />
              <Button variant="secondary" onClick={onEdit} disabled={deleting}>
                Edit
              </Button>
            </>
          )}
          <Button onClick={(event) => onConnectMenu(event.currentTarget.getBoundingClientRect())} disabled={deleting || !connectable} aria-haspopup="menu">
            Connect
          </Button>
        </div>
      </header>

      <div className={styles.body}>
        {error && <Alert>{error}</Alert>}
        {!connectable && (
          <p className={styles.muted} role="note">
            {host.services.length === 0
              ? `There’s nothing to connect with yet: add a service, such as SSH${host.canManage ? ', with Edit' : ''}.`
              : 'None of this host’s services can be opened: no loaded plugin opens a session on them here.'}
          </p>
        )}

        <section className={styles.section} aria-labelledby="overview-title">
          <h2 id="overview-title" className={styles.sectionTitle}>
            Overview
          </h2>
          <dl className={styles.properties}>
            <dt>Hostname</dt>
            <dd className={styles.mono}>{host.hostname ?? <span className={styles.muted}>—</span>}</dd>
            <dt>IP address</dt>
            <dd className={styles.mono}>{host.ipAddress ?? <span className={styles.muted}>—</span>}</dd>
            <dt>Updated</dt>
            <dd>
              <time dateTime={host.updatedAtUtc}>{dateTimeFormat.format(new Date(host.updatedAtUtc))}</time>
            </dd>
            {host.description && (
              <>
                <dt>Description</dt>
                <dd className={styles.description}>{host.description}</dd>
              </>
            )}
            {host.jump && (
              <>
                <dt>Reached through</dt>
                <dd>
                  {jumpHost ? jumpHost.name : <span className={styles.muted}>A jump host you can’t see</span>}
                  <span className={styles.muted}> · over SSH</span>
                </dd>
              </>
            )}
          </dl>
        </section>

        <section className={styles.section} aria-labelledby="tags-title">
          <h2 id="tags-title" className={styles.sectionTitle}>
            Tags
          </h2>
          {host.tags.length === 0 ? (
            <p className={styles.muted}>No tags. Add tags like site, env or role to group hosts in the tree.</p>
          ) : (
            <ul className={styles.chips} aria-label="Tags">
              {host.tags.map((tag) => (
                <li key={`${tag.name}=${tag.value ?? ''}`}>
                  <span className={styles.chip}>
                    <span className={styles.chipName}>{tag.name}</span>
                    {tag.value && <span className={styles.chipValue}>{tag.value}</span>}
                  </span>
                </li>
              ))}
            </ul>
          )}
        </section>

        <section className={styles.section} aria-labelledby="services-title">
          <div className={styles.sectionHeader}>
            <h2 id="services-title" className={styles.sectionTitle}>
              Services
            </h2>
            {host.services.length > 0 && (
              <Button variant="ghost" size="sm" onClick={() => void check()} loading={checking}>
                Check now
              </Button>
            )}
          </div>
          {host.services.length === 0 ? (
            <p className={styles.muted}>No services. Edit the host to add SSH, RDP and more.</p>
          ) : (
            <ul className={styles.rows}>
              {host.services.map((service, index) => {
                const type = catalog.serviceTypes.get(service.type)
                return (
                  <li key={service.id} className={styles.row}>
                    <Badge variant={index === 0 ? 'accent' : 'neutral'}>{type?.label ?? service.type}</Badge>
                    <span className={styles.rowMain}>
                      {service.label ?? type?.label ?? service.type}
                      {!service.available && <span className={styles.muted}> · No loaded plugin provides it</span>}
                    </span>
                    <span className={`${styles.mono} ${styles.muted}`}>
                      {service.address ? `${service.address}, ` : ''}
                      {service.port ? `port ${service.port}` : type?.defaultPort ? `port ${type.defaultPort} (default)` : ''}
                    </span>
                    <ServiceStatus check={health.get(service.id)} />
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        <section className={styles.section} aria-labelledby="credentials-title">
          <h2 id="credentials-title" className={styles.sectionTitle}>
            Credentials
          </h2>
          {host.credentials.length === 0 ? (
            <p className={styles.muted}>No credentials saved for this host.</p>
          ) : (
            <ul className={styles.rows}>
              {host.credentials.map((credential) => {
                const account = credential.username ? (credential.domain ? `${credential.domain}\\${credential.username}` : credential.username) : null
                return (
                  <li key={credential.id} className={styles.row}>
                    <div className={styles.rowMain}>
                      <div className={styles.mono}>{account ?? credentialName(credential)}</div>
                      {account && credential.label && <div className={styles.muted}>{credential.label}</div>}
                      {credential.notes && <div className={styles.muted}>{credential.notes}</div>}
                      {credential.serviceIndexes !== null && (
                        <div className={styles.muted}>
                          Used with:{' '}
                          {host.services
                            .filter((_, index) => usedWith(credential, index))
                            .map((service) => service.label ?? catalog.serviceTypes.get(service.type)?.label ?? service.type)
                            .join(', ') || 'no services'}
                        </div>
                      )}
                    </div>
                    <div className={styles.badges}>
                      {credential.hasPassword && <Badge>Password</Badge>}
                      {credential.hasPrivateKey && <Badge>SSH key</Badge>}
                      {!credential.hasPassword && !credential.hasPrivateKey && <span className={styles.muted}>No secret</span>}
                    </div>
                  </li>
                )
              })}
            </ul>
          )}
        </section>

        {host.trustedKeys.length > 0 && (
          <section className={styles.section} aria-labelledby="host-keys-title">
            <h2 id="host-keys-title" className={styles.sectionTitle}>
              Trusted keys and certificates
            </h2>
            <ul className={styles.rows}>
              {host.trustedKeys.map((key) => (
                <li key={key.id} className={styles.row}>
                  <div className={styles.rowMain}>
                    <div className={`${styles.mono} ${styles.fingerprint}`}>SHA256:{key.fingerprint}</div>
                    <div className={styles.muted}>
                      {key.kind === 'tls-certificate' ? 'Certificate' : key.algorithm}
                      {key.address ? ` · ${key.address}` : ''} · port {key.port} · trusted {dateTimeFormat.format(new Date(key.trustedAtUtc))}
                    </div>
                  </div>
                  {host.canManage && (
                    <Button variant="ghost" size="sm" onClick={() => void removeKey(key.id)} aria-label={`Remove the ${key.algorithm} key for port ${key.port}`}>
                      Remove
                    </Button>
                  )}
                </li>
              ))}
            </ul>
          </section>
        )}
      </div>
    </article>
  )
}

/** A service's latest check, as 1.x showed it: up and how fast, or down and why, plus its certificate's expiry. */
function ServiceStatus({ check }: { check: ServiceHealth | undefined }) {
  if (!check) {
    return null
  }
  const days = check.certificateExpiresAtUtc ? daysUntil(check.certificateExpiresAtUtc) : null
  return (
    <span className={styles.status} title={`Checked ${dateTimeFormat.format(new Date(check.checkedAtUtc))}`}>
      {check.state === 'up' ? (
        <span className={styles.up}>Up · {check.latencyMs} ms</span>
      ) : (
        <span className={styles.down}>Down{check.reason ? ` · ${DOWN_REASONS[check.reason] ?? check.reason}` : ''}</span>
      )}
      {days !== null && (
        <span className={days <= CERTIFICATE_WARNING_DAYS ? styles.down : styles.muted} title={check.certificateSubject ?? undefined}>
          {' · '}
          {days < 0 ? `Certificate expired ${-days} days ago` : `Certificate expires in ${days} days`}
        </span>
      )}
    </span>
  )
}
