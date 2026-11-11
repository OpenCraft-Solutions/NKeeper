import { useState, type FormEvent, type ReactNode } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { credentialName, serviceName } from './hosts'
import type { Host, HostCredential, HostService, ServiceSetting, ServiceTypeInfo } from './types'

/**
 * Creates and edits a host (docs/architecture.md §7.14): its own fields, its services with settings forms
 * drawn from their types' schemas, its credentials (secrets write-only) and its jump host. The server's
 * validation is what counts: its field errors are shown next to their fields.
 */

interface ServiceDraft {
  key: string
  id: string | null
  type: string
  label: string
  port: string
  address: string
  settings: Record<string, unknown>
  available: boolean
}

interface CredentialDraft {
  key: string
  id: string | null
  label: string
  username: string
  domain: string
  notes: string
  hasPassword: boolean
  hasPrivateKey: boolean
  password: string
  clearPassword: boolean
  privateKey: string
  clearPrivateKey: boolean
  allServices: boolean
  serviceKeys: string[]
}

interface Draft {
  name: string
  hostname: string
  ipAddress: string
  description: string
  tags: { key: string; name: string; value: string }[]
  services: ServiceDraft[]
  credentials: CredentialDraft[]
  jump: { hostId: string; serviceId: string; credentialId: string } | null
}

let keys = 0
const newKey = () => `k${++keys}`

function draftOf(host: Host | null): Draft {
  if (!host) {
    return { name: '', hostname: '', ipAddress: '', description: '', tags: [], services: [], credentials: [], jump: null }
  }
  const services = host.services.map(
    (s): ServiceDraft => ({
      key: newKey(),
      id: s.id,
      type: s.type,
      label: s.label ?? '',
      port: s.port?.toString() ?? '',
      address: s.address ?? '',
      settings: { ...s.settings },
      available: s.available,
    }),
  )
  return {
    name: host.name,
    hostname: host.hostname ?? '',
    ipAddress: host.ipAddress ?? '',
    description: host.description ?? '',
    tags: host.tags.map((t) => ({ key: newKey(), name: t.name, value: t.value ?? '' })),
    services,
    credentials: host.credentials.map((c) => credentialDraft(c, services)),
    jump: host.jump ? { hostId: host.jump.hostId, serviceId: host.jump.serviceId ?? '', credentialId: host.jump.credentialId ?? '' } : null,
  }
}

function credentialDraft(c: HostCredential, services: ServiceDraft[]): CredentialDraft {
  return {
    key: newKey(),
    id: c.id,
    label: c.label ?? '',
    username: c.username ?? '',
    domain: c.domain ?? '',
    notes: c.notes ?? '',
    hasPassword: c.hasPassword,
    hasPrivateKey: c.hasPrivateKey,
    password: '',
    clearPassword: false,
    privateKey: '',
    clearPrivateKey: false,
    allServices: c.serviceIndexes === null,
    serviceKeys: (c.serviceIndexes ?? []).map((i) => services[i]?.key).filter((k): k is string => !!k),
  }
}

const orNull = (value: string) => (value.trim() === '' ? null : value.trim())

/** The request body: services in order, and each credential's services as positions in that order. */
function requestOf(draft: Draft, version: number | null) {
  const positions = new Map(draft.services.map((s, i) => [s.key, i]))
  return {
    name: draft.name.trim(),
    hostname: orNull(draft.hostname),
    ipAddress: orNull(draft.ipAddress),
    description: orNull(draft.description),
    tags: draft.tags.filter((t) => t.name.trim() !== '').map((t) => ({ name: t.name.trim(), value: orNull(t.value) })),
    services: draft.services.map((s) => ({
      id: s.id,
      type: s.type,
      label: orNull(s.label),
      port: s.port.trim() === '' ? null : Number(s.port),
      address: orNull(s.address),
      settings: s.settings,
    })),
    credentials: draft.credentials.map((c) => ({
      id: c.id,
      label: orNull(c.label),
      username: orNull(c.username),
      domain: orNull(c.domain),
      notes: orNull(c.notes),
      password: c.password === '' ? null : c.password,
      clearPassword: c.clearPassword,
      privateKey: c.privateKey.trim() === '' ? null : c.privateKey,
      clearPrivateKey: c.clearPrivateKey,
      serviceIndexes: c.allServices ? null : c.serviceKeys.map((k) => positions.get(k)).filter((i): i is number => i !== undefined),
    })),
    jump: draft.jump && draft.jump.hostId ? { hostId: draft.jump.hostId, serviceId: draft.jump.serviceId || null, credentialId: draft.jump.credentialId || null } : null,
    ...(version === null ? {} : { version }),
  }
}

type Errors = Record<string, string[]>

function FieldError({ errors, field }: { errors: Errors; field: string }) {
  const messages = errors[field]
  return messages ? <span className="nk-field-error">{messages.join(' ')}</span> : null
}

export interface HostEditorProps {
  /** The host to edit; null to create one. */
  host: Host | null
  /** Every host the user can see, for the jump host. */
  hosts: Host[]
  serviceTypes: Map<string, ServiceTypeInfo>
  onSaved: (host: Host) => void
  onCancel: () => void
  /** Reloads the host after a conflict. */
  onReload: () => void
}

export function HostEditor({ host, hosts, serviceTypes, onSaved, onCancel, onReload }: HostEditorProps) {
  const [draft, setDraft] = useState<Draft>(() => draftOf(host))
  const [errors, setErrors] = useState<Errors>({})
  const [message, setMessage] = useState<string | null>(null)
  const [conflict, setConflict] = useState(false)
  const [busy, setBusy] = useState(false)
  const types = [...serviceTypes.values()]

  const update = (change: Partial<Draft>) => setDraft((d) => ({ ...d, ...change }))
  const updateService = (key: string, change: Partial<ServiceDraft>) =>
    setDraft((d) => ({ ...d, services: d.services.map((s) => (s.key === key ? { ...s, ...change } : s)) }))
  const updateCredential = (key: string, change: Partial<CredentialDraft>) =>
    setDraft((d) => ({ ...d, credentials: d.credentials.map((c) => (c.key === key ? { ...c, ...change } : c)) }))

  async function submit(event: FormEvent) {
    event.preventDefault()
    setErrors({})
    setMessage(null)
    setConflict(false)
    // A port that isn't a whole number would be sent as no port at all, so it's caught here.
    const badPorts = Object.fromEntries(
      draft.services.flatMap((s, i) => (s.port.trim() === '' || /^\d{1,5}$/.test(s.port.trim()) ? [] : [[`services[${i}].port`, ['Enter a port from 1 to 65535.']]])),
    )
    if (Object.keys(badPorts).length > 0) {
      setErrors(badPorts)
      setMessage('Some fields need fixing.')
      return
    }
    setBusy(true)
    try {
      const body = requestOf(draft, host?.version ?? null)
      onSaved(host ? await api.put<Host>(`/api/hosts/${host.id}`, body) : await api.post<Host>('/api/hosts', body))
    } catch (error) {
      if (error instanceof ApiError) {
        setErrors(error.problem?.errors ?? {})
        setConflict(error.status === 409 && error.code === 'version_conflict')
        setMessage(error.status === 409 && error.code === 'version_conflict' ? 'Someone else changed this host since you started editing it.' : error.message)
      } else {
        setMessage('The host could not be saved.')
      }
    } finally {
      setBusy(false)
    }
  }

  const jumpHosts = hosts.filter((h) => h.id !== host?.id && !h.jump)
  const jumpHost = draft.jump ? hosts.find((h) => h.id === draft.jump!.hostId) : undefined
  const fieldErrors = Object.entries(errors).filter(([, messages]) => messages.length > 0)

  return (
    <form className="nk-form nk-editor" onSubmit={(e) => void submit(e)} aria-label={host ? `Edit ${host.name}` : 'New host'}>
      <h2>{host ? `Edit ${host.name}` : 'New host'}</h2>
      {message && (
        <div className="nk-alert" role="alert">
          <p>{message}</p>
          {fieldErrors.length > 0 && (
            <ul>
              {fieldErrors.map(([field, messages]) => (
                <li key={field}>
                  <code>{field}</code>: {messages.join(' ')}
                </li>
              ))}
            </ul>
          )}
          {conflict && (
            <button type="button" onClick={onReload}>
              Reload the host
            </button>
          )}
        </div>
      )}

      <Section title="Host">
        <label>
          Name
          <input value={draft.name} onChange={(e) => update({ name: e.target.value })} required maxLength={200} />
          <FieldError errors={errors} field="name" />
        </label>
        <div className="nk-row">
          <label>
            Hostname
            <input value={draft.hostname} onChange={(e) => update({ hostname: e.target.value })} />
            <FieldError errors={errors} field="hostname" />
          </label>
          <label>
            IP address
            <input value={draft.ipAddress} onChange={(e) => update({ ipAddress: e.target.value })} />
            <FieldError errors={errors} field="ipAddress" />
          </label>
        </div>
        <label>
          Description
          <textarea rows={2} value={draft.description} onChange={(e) => update({ description: e.target.value })} />
          <FieldError errors={errors} field="description" />
        </label>
      </Section>

      <Section title="Tags">
        {draft.tags.map((tag, i) => (
          <div className="nk-row" key={tag.key}>
            <label>
              Tag name
              <input value={tag.name} onChange={(e) => update({ tags: draft.tags.map((t) => (t.key === tag.key ? { ...t, name: e.target.value } : t)) })} />
              <FieldError errors={errors} field={`tags[${i}].name`} />
            </label>
            <label>
              Value
              <input value={tag.value} onChange={(e) => update({ tags: draft.tags.map((t) => (t.key === tag.key ? { ...t, value: e.target.value } : t)) })} />
            </label>
            <button type="button" className="nk-remove" onClick={() => update({ tags: draft.tags.filter((t) => t.key !== tag.key) })}>
              Remove tag
            </button>
          </div>
        ))}
        <button type="button" onClick={() => update({ tags: [...draft.tags, { key: newKey(), name: '', value: '' }] })}>
          Add tag
        </button>
      </Section>

      <Section title="Services">
        {draft.services.map((service, i) => {
          const type = serviceTypes.get(service.type)
          return (
            <fieldset key={service.key} className="nk-item">
              <legend>{type?.label ?? service.type}</legend>
              {!service.available || !type ? (
                <p className="nk-muted">No enabled plugin provides this type, so the service can only be kept as it is, or removed.</p>
              ) : (
                <>
                  <div className="nk-row">
                    <label>
                      Label
                      <input value={service.label} onChange={(e) => updateService(service.key, { label: e.target.value })} maxLength={100} />
                      <FieldError errors={errors} field={`services[${i}].label`} />
                    </label>
                    <label>
                      Port
                      <input
                        inputMode="numeric"
                        placeholder={type.defaultPort?.toString() ?? ''}
                        value={service.port}
                        onChange={(e) => updateService(service.key, { port: e.target.value })}
                      />
                      <FieldError errors={errors} field={`services[${i}].port`} />
                    </label>
                    <label>
                      Address
                      <input placeholder="The host’s own" value={service.address} onChange={(e) => updateService(service.key, { address: e.target.value })} />
                      <FieldError errors={errors} field={`services[${i}].address`} />
                    </label>
                  </div>
                  <SettingsFields
                    settings={type.settings}
                    values={service.settings}
                    errors={errors}
                    field={`services[${i}].settings`}
                    onChange={(settings) => updateService(service.key, { settings })}
                  />
                </>
              )}
              <FieldError errors={errors} field={`services[${i}]`} />
              <button
                type="button"
                className="nk-remove"
                onClick={() =>
                  setDraft((d) => ({
                    ...d,
                    services: d.services.filter((s) => s.key !== service.key),
                    credentials: d.credentials.map((c) => ({ ...c, serviceKeys: c.serviceKeys.filter((k) => k !== service.key) })),
                  }))
                }
              >
                Remove service
              </button>
            </fieldset>
          )
        })}
        {types.length === 0 ? (
          <p className="nk-muted">No plugin provides a service type yet.</p>
        ) : (
          <AddService
            types={types}
            onAdd={(type) =>
              update({
                services: [
                  ...draft.services,
                  // A required switch shows as off, so it starts as off rather than unset.
                  {
                    key: newKey(),
                    id: null,
                    type: type.id,
                    label: '',
                    port: '',
                    address: '',
                    settings: Object.fromEntries(type.settings.filter((s) => s.kind === 'switch' && s.required).map((s) => [s.key, false])),
                    available: true,
                  },
                ],
              })
            }
          />
        )}
      </Section>

      <Section title="Credentials">
        {draft.credentials.map((credential, i) => (
          <fieldset key={credential.key} className="nk-item">
            <legend>{credential.label || credential.username || 'New credential'}</legend>
            <div className="nk-row">
              <label>
                Label
                <input value={credential.label} onChange={(e) => updateCredential(credential.key, { label: e.target.value })} />
              </label>
              <label>
                User name
                <input autoComplete="off" value={credential.username} onChange={(e) => updateCredential(credential.key, { username: e.target.value })} />
                <FieldError errors={errors} field={`credentials[${i}].username`} />
              </label>
              <label>
                Domain
                <input autoComplete="off" value={credential.domain} onChange={(e) => updateCredential(credential.key, { domain: e.target.value })} />
              </label>
            </div>
            <Secret
              label="Password"
              has={credential.hasPassword}
              value={credential.password}
              clear={credential.clearPassword}
              onChange={(password) => updateCredential(credential.key, { password, clearPassword: false })}
              onClear={(clearPassword) => updateCredential(credential.key, { clearPassword, password: '' })}
              error={<FieldError errors={errors} field={`credentials[${i}].password`} />}
            />
            <Secret
              label="Private key"
              multiline
              has={credential.hasPrivateKey}
              value={credential.privateKey}
              clear={credential.clearPrivateKey}
              onChange={(privateKey) => updateCredential(credential.key, { privateKey, clearPrivateKey: false })}
              onClear={(clearPrivateKey) => updateCredential(credential.key, { clearPrivateKey, privateKey: '' })}
              error={<FieldError errors={errors} field={`credentials[${i}].privateKey`} />}
            />
            <fieldset className="nk-inline">
              <legend>Used with</legend>
              <label>
                <input type="checkbox" checked={credential.allServices} onChange={(e) => updateCredential(credential.key, { allServices: e.target.checked })} /> All
                services
              </label>
              {!credential.allServices &&
                draft.services.map((service) => (
                  <label key={service.key}>
                    <input
                      type="checkbox"
                      checked={credential.serviceKeys.includes(service.key)}
                      onChange={(e) =>
                        updateCredential(credential.key, {
                          serviceKeys: e.target.checked ? [...credential.serviceKeys, service.key] : credential.serviceKeys.filter((k) => k !== service.key),
                        })
                      }
                    />{' '}
                    {service.label || serviceTypes.get(service.type)?.label || service.type}
                  </label>
                ))}
              <FieldError errors={errors} field={`credentials[${i}].serviceIndexes`} />
            </fieldset>
            <label>
              Notes
              <textarea rows={2} value={credential.notes} onChange={(e) => updateCredential(credential.key, { notes: e.target.value })} />
            </label>
            <FieldError errors={errors} field={`credentials[${i}]`} />
            <button type="button" className="nk-remove" onClick={() => update({ credentials: draft.credentials.filter((c) => c.key !== credential.key) })}>
              Remove credential
            </button>
          </fieldset>
        ))}
        <button
          type="button"
          onClick={() =>
            update({
              credentials: [
                ...draft.credentials,
                {
                  key: newKey(),
                  id: null,
                  label: '',
                  username: '',
                  domain: '',
                  notes: '',
                  hasPassword: false,
                  hasPrivateKey: false,
                  password: '',
                  clearPassword: false,
                  privateKey: '',
                  clearPrivateKey: false,
                  allServices: true,
                  serviceKeys: [],
                },
              ],
            })
          }
        >
          Add credential
        </button>
      </Section>

      <Section title="Jump host">
        <label>
          Reach this host through
          <select
            value={draft.jump?.hostId ?? ''}
            onChange={(e) => update({ jump: e.target.value ? { hostId: e.target.value, serviceId: '', credentialId: '' } : null })}
          >
            <option value="">No jump host</option>
            {jumpHosts.map((h) => (
              <option key={h.id} value={h.id}>
                {h.name}
              </option>
            ))}
            {draft.jump && !jumpHost && <option value={draft.jump.hostId}>A host you can’t see</option>}
          </select>
          <FieldError errors={errors} field="jump.hostId" />
        </label>
        {draft.jump && jumpHost && (
          <div className="nk-row">
            <label>
              Its service
              <select value={draft.jump.serviceId} onChange={(e) => update({ jump: { ...draft.jump!, serviceId: e.target.value } })}>
                <option value="">Choose a service</option>
                {jumpHost.services.map((s: HostService) => (
                  <option key={s.id} value={s.id}>
                    {serviceName(s, serviceTypes)}
                  </option>
                ))}
              </select>
              <FieldError errors={errors} field="jump.serviceId" />
            </label>
            <label>
              Its credential
              <select value={draft.jump.credentialId} onChange={(e) => update({ jump: { ...draft.jump!, credentialId: e.target.value } })}>
                <option value="">Choose a credential</option>
                {jumpHost.credentials.map((c) => (
                  <option key={c.id} value={c.id}>
                    {credentialName(c)}
                  </option>
                ))}
              </select>
              <FieldError errors={errors} field="jump.credentialId" />
            </label>
          </div>
        )}
      </Section>

      <div className="nk-actions">
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="nk-primary" disabled={busy}>
          {host ? 'Save' : 'Create host'}
        </button>
      </div>
    </form>
  )
}

function Section({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="nk-section">
      <h3>{title}</h3>
      {children}
    </section>
  )
}

function AddService({ types, onAdd }: { types: ServiceTypeInfo[]; onAdd: (type: ServiceTypeInfo) => void }) {
  const [typeId, setTypeId] = useState(types[0]?.id ?? '')
  return (
    <div className="nk-row nk-add">
      <label>
        Service type
        <select value={typeId} onChange={(e) => setTypeId(e.target.value)}>
          {types.map((t) => (
            <option key={t.id} value={t.id}>
              {t.label}
            </option>
          ))}
        </select>
      </label>
      <button
        type="button"
        onClick={() => {
          const type = types.find((t) => t.id === typeId)
          if (type) {
            onAdd(type)
          }
        }}
      >
        Add service
      </button>
    </div>
  )
}

/** A write-only secret: the editor says whether one is set, and can replace or clear it. */
function Secret(props: {
  label: string
  has: boolean
  value: string
  clear: boolean
  multiline?: boolean
  onChange: (value: string) => void
  onClear: (clear: boolean) => void
  error: ReactNode
}) {
  const placeholder = props.has ? (props.clear ? 'Will be cleared' : 'Set. Type to replace it') : 'Not set'
  return (
    <div className="nk-secret">
      <label>
        {props.label}
        {props.multiline ? (
          <textarea className="nk-mono" rows={3} placeholder={placeholder} value={props.value} onChange={(e) => props.onChange(e.target.value)} />
        ) : (
          <input type="password" autoComplete="new-password" placeholder={placeholder} value={props.value} onChange={(e) => props.onChange(e.target.value)} />
        )}
        {props.error}
      </label>
      {props.has && (
        <label className="nk-check">
          <input type="checkbox" checked={props.clear} onChange={(e) => props.onClear(e.target.checked)} /> Clear the {props.label.toLowerCase()}
        </label>
      )}
    </div>
  )
}

/** A service's settings, drawn from its type's schema (docs/architecture.md §7.4). */
export function SettingsFields({
  settings,
  values,
  errors,
  field,
  onChange,
}: {
  settings: ServiceSetting[]
  values: Record<string, unknown>
  errors: Errors
  field: string
  onChange: (values: Record<string, unknown>) => void
}) {
  if (settings.length === 0) {
    return null
  }
  const set = (key: string, value: unknown) => {
    const next = { ...values }
    if (value === null || value === '') {
      delete next[key]
    } else {
      next[key] = value
    }
    onChange(next)
  }
  return (
    <div className="nk-settings">
      {settings.map((setting) => {
        const value = values[setting.key]
        const error = <FieldError errors={errors} field={`${field}.${setting.key}`} />
        switch (setting.kind) {
          case 'switch':
            return (
              <label key={setting.key} className="nk-check">
                <input type="checkbox" checked={value === true} onChange={(e) => set(setting.key, e.target.checked)} /> {setting.label}
                {error}
              </label>
            )
          case 'choice':
            return (
              <label key={setting.key}>
                {setting.label}
                <select value={typeof value === 'string' ? value : ''} required={setting.required} onChange={(e) => set(setting.key, e.target.value)}>
                  <option value="">{setting.required ? 'Choose…' : 'Not set'}</option>
                  {(setting.choices ?? []).map((choice) => (
                    <option key={choice} value={choice}>
                      {choice}
                    </option>
                  ))}
                </select>
                {error}
              </label>
            )
          case 'number':
            return (
              <label key={setting.key}>
                {setting.label}
                <input
                  type="number"
                  step={1}
                  min={setting.minimum ?? undefined}
                  max={setting.maximum ?? undefined}
                  required={setting.required}
                  value={typeof value === 'number' ? value : ''}
                  onChange={(e) => set(setting.key, e.target.value === '' ? null : Number(e.target.value))}
                />
                {error}
              </label>
            )
          default:
            return (
              <label key={setting.key}>
                {setting.label}
                <input
                  maxLength={setting.maxLength ?? undefined}
                  required={setting.required}
                  value={typeof value === 'string' ? value : ''}
                  onChange={(e) => set(setting.key, e.target.value)}
                />
                {error}
              </label>
            )
        }
      })}
    </div>
  )
}
