import { useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { connectMenu, type Catalog, type ConnectEntry, type ConnectGroup } from './catalog'
import { ConnectMenu } from './ConnectMenu'
import { credentialName, hostAddress, serviceName, usedWith } from './hosts'
import { expiryText } from './health'
import type { Host, ServiceHealth } from './types'

const DOWN_REASONS: Record<string, string> = { refused: 'refused', timeout: 'timed out', unreachable: 'unreachable' }

/** A host's details, its Connect menu, and for a manager its edit, delete and trusted keys (docs/architecture.md §7.14). */
export function HostDetails({
  host,
  hosts,
  catalog,
  health,
  onEdit,
  onDeleted,
  onKeyRemoved,
  onCheck,
  onConnect,
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
  onConnect: (group: ConnectGroup, entry: ConnectEntry) => void
}) {
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)
  const jumpHost = host.jump ? hosts.find((h) => h.id === host.jump!.hostId) : undefined
  const groups = connectMenu(host, catalog)

  async function remove() {
    setError(null)
    try {
      await api.delete(`/api/hosts/${host.id}`)
      onDeleted()
    } catch (caught) {
      setError(caught instanceof ApiError ? caught.message : 'The host could not be deleted.')
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
    <article className="nk-details" aria-label={host.name}>
      <header className="nk-details-header">
        <div>
          <h2>{host.name}</h2>
          <p className="nk-muted">{[hostAddress(host), jumpHost ? `through ${jumpHost.name}` : host.jump ? 'through a jump host' : null].filter(Boolean).join(' · ')}</p>
        </div>
        <div className="nk-actions">
          <ConnectMenu groups={groups} onChoose={onConnect} />
          <button type="button" onClick={() => void check()} disabled={checking}>
            Check now
          </button>
          {host.canManage && (
            <button type="button" onClick={onEdit}>
              Edit
            </button>
          )}
          {host.canManage &&
            (confirmDelete ? (
              <>
                <button type="button" className="nk-danger" onClick={() => void remove()}>
                  Delete {host.name}
                </button>
                <button type="button" onClick={() => setConfirmDelete(false)}>
                  Keep it
                </button>
              </>
            ) : (
              <button type="button" onClick={() => setConfirmDelete(true)}>
                Delete
              </button>
            ))}
        </div>
      </header>
      {error && (
        <p className="nk-alert" role="alert">
          {error}
        </p>
      )}
      {groups.length === 0 && (
        <p className="nk-muted" role="note">
          {host.services.length === 0
            ? `There’s nothing to connect with yet: add a service, such as SSH${host.canManage ? ', with Edit' : ''}.`
            : 'None of this host’s services can be opened: no loaded plugin opens a session on them here.'}
        </p>
      )}
      {host.description && <p>{host.description}</p>}
      {host.tags.length > 0 && (
        <ul className="nk-tags" aria-label="Tags">
          {host.tags.map((t) => (
            <li key={`${t.name}=${t.value ?? ''}`}>{t.value === null ? t.name : `${t.name}: ${t.value}`}</li>
          ))}
        </ul>
      )}

      <h3>Services</h3>
      {host.services.length === 0 ? (
        <p className="nk-muted">No services.</p>
      ) : (
        <table className="nk-table">
          <thead>
            <tr>
              <th scope="col">Service</th>
              <th scope="col">Address</th>
              <th scope="col">Status</th>
              <th scope="col">Credentials</th>
            </tr>
          </thead>
          <tbody>
            {host.services.map((service, index) => {
              const check = health.get(service.id)
              const credentials = host.credentials.filter((c) => usedWith(c, index))
              return (
                <tr key={service.id}>
                  <td>
                    {serviceName(service, catalog.serviceTypes)}
                    {!service.available && <span className="nk-muted"> (no plugin provides it)</span>}
                  </td>
                  <td>{service.address ?? hostAddress(host) ?? '—'}</td>
                  <td>
                    {!check
                      ? '—'
                      : check.state === 'up'
                        ? `Up, ${check.latencyMs} ms${check.certificateExpiresAtUtc ? `, certificate expires ${expiryText(check.certificateExpiresAtUtc)}` : ''}`
                        : `Down${check.reason ? `: ${DOWN_REASONS[check.reason] ?? check.reason}` : ''}`}
                  </td>
                  <td>{credentials.length === 0 ? '—' : credentials.map(credentialName).join(', ')}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      )}

      <h3>Trusted server keys</h3>
      {host.trustedKeys.length === 0 ? (
        <p className="nk-muted">None yet. A key is trusted when someone first connects and accepts it.</p>
      ) : (
        <table className="nk-table">
          <thead>
            <tr>
              <th scope="col">For</th>
              <th scope="col">Algorithm</th>
              <th scope="col">Fingerprint</th>
              {host.canManage && <th scope="col" aria-label="Remove" />}
            </tr>
          </thead>
          <tbody>
            {host.trustedKeys.map((key) => (
              <tr key={key.id}>
                <td>
                  {key.address ?? hostAddress(host)} :{key.port}
                </td>
                <td>{key.algorithm}</td>
                <td className="nk-mono">SHA256:{key.fingerprint}</td>
                {host.canManage && (
                  <td>
                    <button type="button" onClick={() => void removeKey(key.id)} aria-label={`Remove the ${key.algorithm} key for port ${key.port}`}>
                      Remove
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </article>
  )
}
