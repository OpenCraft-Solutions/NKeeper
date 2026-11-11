import { useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { credentialName, serviceName } from './hosts'
import type { Host, HostCredential, HostService, ServiceSetting, ServiceTypeInfo } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { Checkbox } from './ui/Checkbox'
import creds from './ui/CredentialsEditor.module.css'
import panel from './ui/HostPanel.module.css'
import list from './ui/ListEditor.module.css'
import { PasswordField } from './ui/PasswordField'
import { TextArea } from './ui/TextArea'
import { TextField } from './ui/TextField'

/**
 * Creates and edits a host (docs/architecture.md §7.14), laid out as 1.x's host editor: its own fields and jump host,
 * its tags, its services with settings drawn from their types' schemas, and its credentials (secrets write-only). The server's
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

  const title = host ? `Edit ${host.name}` : 'New host'
  const firstError = (field: string) => errors[field]?.join(' ')
  const removeService = (key: string) =>
    setDraft((d) => ({
      ...d,
      services: d.services.filter((s) => s.key !== key),
      credentials: d.credentials.map((c) => ({ ...c, serviceKeys: c.serviceKeys.filter((k) => k !== key) })),
    }))
  const moveService = (index: number, step: -1 | 1) =>
    setDraft((d) => {
      const services = [...d.services]
      const [moved] = services.splice(index, 1)
      if (moved) {
        services.splice(index + step, 0, moved)
      }
      return { ...d, services }
    })
  const newService = (type: ServiceTypeInfo): ServiceDraft => ({
    key: newKey(),
    id: null,
    type: type.id,
    label: '',
    port: '',
    address: '',
    // A required switch shows as off, so it starts as off rather than unset.
    settings: Object.fromEntries(type.settings.filter((s) => s.kind === 'switch' && s.required).map((s) => [s.key, false])),
    available: true,
  })

  return (
    <form className={panel.panel} onSubmit={(e) => void submit(e)} noValidate aria-label={title}>
      <header className={panel.header}>
        <div className={panel.heading}>
          <h1 className={panel.title}>{title}</h1>
          {!host && <p className={panel.subtitle}>Add a server or device to the inventory.</p>}
        </div>
      </header>

      <div className={panel.body}>
        {message && (
          <Alert>
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
              <Button variant="secondary" size="sm" onClick={onReload}>
                Reload the host
              </Button>
            )}
          </Alert>
        )}

        <section className={panel.section} aria-labelledby="host-details-title">
          <h2 id="host-details-title" className={panel.sectionTitle}>
            Details
          </h2>
          <div className={panel.formGrid}>
            <TextField
              className={panel.full}
              label="Name"
              value={draft.name}
              onChange={(e) => update({ name: e.target.value })}
              error={firstError('name')}
              maxLength={200}
              autoComplete="off"
              autoFocus
              required
            />
            <TextField
              label="Hostname"
              value={draft.hostname}
              onChange={(e) => update({ hostname: e.target.value })}
              error={firstError('hostname')}
              placeholder="server01.example.com"
              maxLength={253}
              autoComplete="off"
              spellCheck={false}
            />
            <TextField
              label="IP address"
              value={draft.ipAddress}
              onChange={(e) => update({ ipAddress: e.target.value })}
              error={firstError('ipAddress')}
              placeholder="10.0.0.10"
              maxLength={45}
              autoComplete="off"
              spellCheck={false}
            />
            <TextArea
              className={panel.full}
              label="Description"
              value={draft.description}
              onChange={(e) => update({ description: e.target.value })}
              error={firstError('description')}
              maxLength={4000}
              rows={2}
            />
            <div className={`${panel.full} ${panel.jump}`}>
              <div className={panel.field}>
                <label htmlFor="jump-host" className={panel.fieldLabel}>
                  Reach through
                </label>
                <select
                  id="jump-host"
                  className={list.input}
                  value={draft.jump?.hostId ?? ''}
                  onChange={(e) => update({ jump: e.target.value ? { hostId: e.target.value, serviceId: '', credentialId: '' } : null })}
                  aria-invalid={firstError('jump.hostId') ? true : undefined}
                >
                  <option value="">Directly (no jump host)</option>
                  {jumpHosts.map((h) => (
                    <option key={h.id} value={h.id}>
                      {h.name}
                    </option>
                  ))}
                  {draft.jump && !jumpHost && <option value={draft.jump.hostId}>A host you can’t see</option>}
                </select>
                {firstError('jump.hostId') && <p className={panel.fieldError}>{firstError('jump.hostId')}</p>}
                <p className={panel.fieldHint}>Connections to this host are tunnelled through the jump host over SSH, where the session type can go through one.</p>
              </div>
              {draft.jump && jumpHost && (
                <>
                  <div className={panel.field}>
                    <label htmlFor="jump-service" className={panel.fieldLabel}>
                      Its SSH service
                    </label>
                    <select id="jump-service" className={list.input} value={draft.jump.serviceId} onChange={(e) => update({ jump: { ...draft.jump!, serviceId: e.target.value } })}>
                      <option value="">Choose a service</option>
                      {jumpHost.services.map((s: HostService) => (
                        <option key={s.id} value={s.id}>
                          {serviceName(s, serviceTypes)}
                        </option>
                      ))}
                    </select>
                    {firstError('jump.serviceId') && <p className={panel.fieldError}>{firstError('jump.serviceId')}</p>}
                  </div>
                  <div className={panel.field}>
                    <label htmlFor="jump-credential" className={panel.fieldLabel}>
                      Sign in to {jumpHost.name} as
                    </label>
                    <select id="jump-credential" className={list.input} value={draft.jump.credentialId} onChange={(e) => update({ jump: { ...draft.jump!, credentialId: e.target.value } })}>
                      <option value="">Choose a credential</option>
                      {jumpHost.credentials.map((c) => (
                        <option key={c.id} value={c.id}>
                          {credentialName(c)}
                        </option>
                      ))}
                    </select>
                    {firstError('jump.credentialId') && <p className={panel.fieldError}>{firstError('jump.credentialId')}</p>}
                  </div>
                </>
              )}
            </div>
          </div>
        </section>

        <section className={panel.section}>
          <fieldset className={list.fieldset}>
            <legend className={list.legend}>Tags</legend>
            <p className={list.hint}>
              Name and optional value, e.g. <code>site: Datacenter A</code> or <code>env: production</code>. Group the host tree by any tag name.
            </p>
            {draft.tags.length === 0 && <p className={list.empty}>No tags yet.</p>}
            <ol className={list.list}>
              {draft.tags.map((tag, i) => {
                const position = `tag ${i + 1}`
                const error = firstError(`tags[${i}].name`) ?? firstError(`tags[${i}].value`)
                const setTag = (change: Partial<typeof tag>) => update({ tags: draft.tags.map((t) => (t.key === tag.key ? { ...t, ...change } : t)) })
                return (
                  <li key={tag.key} className={list.tagRow}>
                    <input
                      className={list.input}
                      value={tag.name}
                      onChange={(e) => setTag({ name: e.target.value.replace(/[:=]/g, '') })}
                      placeholder="Name"
                      maxLength={64}
                      aria-label={`Name for ${position}`}
                      aria-invalid={firstError(`tags[${i}].name`) ? true : undefined}
                      autoComplete="off"
                    />
                    <span className={list.separator} aria-hidden="true">
                      :
                    </span>
                    <input
                      className={list.input}
                      value={tag.value}
                      onChange={(e) => setTag({ value: e.target.value })}
                      placeholder="Value (optional)"
                      maxLength={255}
                      aria-label={`Value for ${position}`}
                      aria-invalid={firstError(`tags[${i}].value`) ? true : undefined}
                      autoComplete="off"
                    />
                    <span />
                    <button type="button" className={list.iconButton} onClick={() => update({ tags: draft.tags.filter((t) => t.key !== tag.key) })} aria-label={`Remove ${position}`}>
                      ×
                    </button>
                    {error && <p className={list.error}>{error}</p>}
                  </li>
                )
              })}
            </ol>
            <div>
              <Button variant="secondary" size="sm" onClick={() => update({ tags: [...draft.tags, { key: newKey(), name: '', value: '' }] })}>
                Add tag
              </Button>
            </div>
          </fieldset>
        </section>

        <section className={panel.section}>
          <fieldset className={list.fieldset}>
            <legend className={list.legend}>Services</legend>
            <p className={list.hint}>How you connect to this host. Leave the port empty to use the default, and the address empty to use the host’s own.</p>
            {draft.services.length === 0 && <p className={list.empty}>{types.length === 0 ? 'No plugin provides a service type yet.' : 'No services yet.'}</p>}
            <ol className={list.list}>
              {draft.services.map((service, i) => {
                const type = serviceTypes.get(service.type)
                const position = `service ${i + 1}`
                const rowError = firstError(`services[${i}]`) ?? firstError(`services[${i}].port`) ?? firstError(`services[${i}].label`) ?? firstError(`services[${i}].address`)
                return (
                  <li key={service.key} className={list.row}>
                    <select
                      className={list.input}
                      value={service.type}
                      disabled={service.id !== null || !type}
                      onChange={(e) => {
                        const next = serviceTypes.get(e.target.value)
                        if (next) {
                          updateService(service.key, { type: next.id, settings: newService(next).settings })
                        }
                      }}
                      aria-label={`Type for ${position}`}
                    >
                      {!type && <option value={service.type}>{service.type} (no plugin provides it)</option>}
                      {types.map((t) => (
                        <option key={t.id} value={t.id}>
                          {t.label}
                        </option>
                      ))}
                    </select>
                    <input
                      className={`${list.input} ${list.port}`}
                      inputMode="numeric"
                      value={service.port}
                      onChange={(e) => updateService(service.key, { port: e.target.value })}
                      placeholder={type?.defaultPort ? String(type.defaultPort) : 'Port'}
                      aria-label={`Port for ${position}`}
                      aria-invalid={firstError(`services[${i}].port`) ? true : undefined}
                      disabled={!service.available || !type}
                    />
                    <input
                      className={`${list.input} ${list.label}`}
                      value={service.label}
                      onChange={(e) => updateService(service.key, { label: e.target.value })}
                      placeholder="Label (optional)"
                      maxLength={100}
                      aria-label={`Label for ${position}`}
                      disabled={!service.available || !type}
                    />
                    <div className={list.actions}>
                      <button type="button" className={list.iconButton} onClick={() => moveService(i, -1)} disabled={i === 0} aria-label={`Move ${position} up`}>
                        ↑
                      </button>
                      <button type="button" className={list.iconButton} onClick={() => moveService(i, 1)} disabled={i === draft.services.length - 1} aria-label={`Move ${position} down`}>
                        ↓
                      </button>
                      <button type="button" className={list.iconButton} onClick={() => removeService(service.key)} aria-label={`Remove ${position}`}>
                        ×
                      </button>
                    </div>
                    {!service.available || !type ? (
                      <p className={`${list.hint} ${list.rowNote}`}>No enabled plugin provides this type, so the service can only be kept as it is, or removed.</p>
                    ) : (
                      <>
                        <label className={list.option}>
                          <span>Address</span>
                          <input
                            className={list.input}
                            value={service.address}
                            onChange={(e) => updateService(service.key, { address: e.target.value })}
                            placeholder="The host’s own"
                            maxLength={253}
                            spellCheck={false}
                            aria-label={`Address for ${position}`}
                          />
                        </label>
                        <SettingsFields
                          settings={type.settings}
                          values={service.settings}
                          errors={errors}
                          field={`services[${i}].settings`}
                          position={position}
                          onChange={(settings) => updateService(service.key, { settings })}
                        />
                      </>
                    )}
                    {rowError && <p className={list.error}>{rowError}</p>}
                  </li>
                )
              })}
            </ol>
            {types.length > 0 && (
              <div>
                <Button variant="secondary" size="sm" onClick={() => update({ services: [...draft.services, newService(types[0]!)] })}>
                  Add service
                </Button>
              </div>
            )}
          </fieldset>
        </section>

        <section className={panel.section}>
          <fieldset className={creds.fieldset}>
            <legend className={creds.legend}>Credentials</legend>
            <p className={creds.hint}>Accounts for signing in to this host. Secrets are encrypted at rest and never shown again after saving.</p>
            {draft.credentials.length === 0 && <p className={creds.hint}>No credentials yet.</p>}
            <ol className={creds.list}>
              {draft.credentials.map((credential, i) => {
                const field = (name: string) => firstError(`credentials[${i}].${name}`)
                const heading = credential.label.trim() || credential.username.trim() || `Credential ${i + 1}`
                return (
                  <li key={credential.key} className={creds.card} aria-label={heading}>
                    <div className={creds.cardHeader}>
                      <span className={creds.cardTitle}>{heading}</span>
                      <Button variant="ghost" size="sm" onClick={() => update({ credentials: draft.credentials.filter((c) => c.key !== credential.key) })}>
                        Remove
                      </Button>
                    </div>
                    {field('id') && <p className={creds.error}>{field('id')}</p>}
                    <div className={creds.grid}>
                      <TextField
                        label="Username"
                        value={credential.username}
                        onChange={(e) => updateCredential(credential.key, { username: e.target.value })}
                        error={field('username')}
                        maxLength={256}
                        autoComplete="off"
                        spellCheck={false}
                      />
                      <TextField
                        label="Domain"
                        value={credential.domain}
                        onChange={(e) => updateCredential(credential.key, { domain: e.target.value })}
                        error={field('domain')}
                        hint="Optional"
                        maxLength={256}
                        autoComplete="off"
                        spellCheck={false}
                      />
                      <TextField
                        label="Label"
                        value={credential.label}
                        onChange={(e) => updateCredential(credential.key, { label: e.target.value })}
                        error={field('label')}
                        hint="Optional, e.g. “Local admin”"
                        maxLength={100}
                        autoComplete="off"
                      />
                      <div className={creds.secret}>
                        <PasswordField
                          label="Password"
                          value={credential.password}
                          onChange={(e) => updateCredential(credential.key, { password: e.target.value, clearPassword: false })}
                          error={field('password')}
                          hint={credential.hasPassword && !credential.clearPassword ? 'Stored. Leave empty to keep it.' : undefined}
                          disabled={credential.clearPassword}
                          maxLength={1024}
                          autoComplete="new-password"
                        />
                        {credential.hasPassword && (
                          <Checkbox
                            label="Remove stored password"
                            checked={credential.clearPassword}
                            onChange={(e) => updateCredential(credential.key, { clearPassword: e.target.checked, password: '' })}
                          />
                        )}
                      </div>
                      <div className={`${creds.secret} ${creds.full}`}>
                        <TextArea
                          label="SSH private key"
                          value={credential.privateKey}
                          onChange={(e) => updateCredential(credential.key, { privateKey: e.target.value, clearPrivateKey: false })}
                          error={field('privateKey')}
                          hint={credential.hasPrivateKey && !credential.clearPrivateKey ? 'Stored. Leave empty to keep it.' : 'Optional. PEM or OpenSSH format.'}
                          disabled={credential.clearPrivateKey}
                          placeholder="-----BEGIN OPENSSH PRIVATE KEY-----"
                          spellCheck={false}
                          rows={2}
                        />
                        {credential.hasPrivateKey && (
                          <Checkbox
                            label="Remove stored key"
                            checked={credential.clearPrivateKey}
                            onChange={(e) => updateCredential(credential.key, { clearPrivateKey: e.target.checked, privateKey: '' })}
                          />
                        )}
                      </div>
                      <fieldset className={`${creds.full} ${creds.useWith}`}>
                        <legend className={creds.useWithLegend}>Use with</legend>
                        <Checkbox
                          label="All services"
                          checked={credential.allServices}
                          // Choosing services starts from all of them ticked: untick the ones it isn't for.
                          onChange={(e) => updateCredential(credential.key, { allServices: e.target.checked, serviceKeys: e.target.checked ? [] : draft.services.map((s) => s.key) })}
                        />
                        {!credential.allServices &&
                          (draft.services.length === 0 ? (
                            <p className={creds.hint}>Add services above, then choose which this credential is for.</p>
                          ) : (
                            <div className={creds.serviceChoices}>
                              {draft.services.map((service) => (
                                <Checkbox
                                  key={service.key}
                                  label={service.label || serviceTypes.get(service.type)?.label || service.type}
                                  checked={credential.serviceKeys.includes(service.key)}
                                  onChange={(e) =>
                                    updateCredential(credential.key, {
                                      serviceKeys: e.target.checked ? [...credential.serviceKeys, service.key] : credential.serviceKeys.filter((k) => k !== service.key),
                                    })
                                  }
                                />
                              ))}
                            </div>
                          ))}
                        {field('serviceIndexes') && <p className={creds.error}>{field('serviceIndexes')}</p>}
                      </fieldset>
                      <TextArea
                        className={creds.full}
                        label="Notes"
                        value={credential.notes}
                        onChange={(e) => updateCredential(credential.key, { notes: e.target.value })}
                        error={field('notes')}
                        maxLength={2000}
                        rows={1}
                      />
                    </div>
                  </li>
                )
              })}
            </ol>
            <div>
              <Button
                variant="secondary"
                size="sm"
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
              </Button>
            </div>
          </fieldset>
        </section>
      </div>

      <footer className={panel.footer}>
        <Button variant="secondary" onClick={onCancel} disabled={busy}>
          Cancel
        </Button>
        <Button type="submit" loading={busy}>
          {host ? 'Save changes' : 'Create host'}
        </Button>
      </footer>
    </form>
  )
}

/** A service's settings, drawn from its type's schema (docs/architecture.md §7.4), as option lines under its row, as 1.x drew them. */
export function SettingsFields({
  settings,
  values,
  errors,
  field,
  position,
  onChange,
}: {
  settings: ServiceSetting[]
  values: Record<string, unknown>
  errors: Errors
  field: string
  /** Which service, for the fields' names: "service 1". */
  position: string
  onChange: (values: Record<string, unknown>) => void
}) {
  const set = (key: string, value: unknown) => {
    const next = { ...values }
    if (value === null || value === '') {
      delete next[key]
    } else {
      next[key] = value
    }
    onChange(next)
  }
  return settings.map((setting) => {
    const value = values[setting.key]
    const error = errors[`${field}.${setting.key}`]?.join(' ')
    const name = `${setting.label} for ${position}`
    const control = (() => {
      switch (setting.kind) {
        case 'switch':
          return (
            <label className={list.toolOption}>
              <input type="checkbox" checked={value === true} onChange={(e) => set(setting.key, e.target.checked)} aria-label={name} />
              {setting.label}
            </label>
          )
        case 'choice':
          return (
            <select className={list.input} value={typeof value === 'string' ? value : ''} required={setting.required} onChange={(e) => set(setting.key, e.target.value)} aria-label={name}>
              <option value="">{setting.required ? 'Choose…' : 'Not set'}</option>
              {(setting.choices ?? []).map((choice) => (
                <option key={choice} value={choice}>
                  {choice}
                </option>
              ))}
            </select>
          )
        case 'number':
          return (
            <input
              className={list.input}
              type="number"
              step={1}
              min={setting.minimum ?? undefined}
              max={setting.maximum ?? undefined}
              required={setting.required}
              value={typeof value === 'number' ? value : ''}
              onChange={(e) => set(setting.key, e.target.value === '' ? null : Number(e.target.value))}
              aria-label={name}
              aria-invalid={error ? true : undefined}
            />
          )
        default:
          return (
            <input
              className={list.input}
              maxLength={setting.maxLength ?? undefined}
              required={setting.required}
              value={typeof value === 'string' ? value : ''}
              onChange={(e) => set(setting.key, e.target.value)}
              aria-label={name}
              aria-invalid={error ? true : undefined}
            />
          )
      }
    })()
    return (
      <div key={setting.key} className={list.option}>
        {setting.kind !== 'switch' && <span>{setting.label}</span>}
        {control}
        {error && <span className={list.error}>{error}</span>}
      </div>
    )
  })
}
