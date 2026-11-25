import { useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Host, ServiceTypeInfo } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import panel from './ui/HostPanel.module.css'
import list from './ui/ListEditor.module.css'
import { PasswordField } from './ui/PasswordField'
import { TextField } from './ui/TextField'

type TagAction = 'add' | 'set' | 'remove'

interface TagChange {
  key: number
  action: TagAction
  name: string
  value: string
}

interface ServiceChange {
  key: number
  action: 'add' | 'remove'
  type: string
  port: string
}

interface CredentialChange {
  key: number
  action: 'add' | 'remove'
  username: string
  domain: string
  password: string
}

const KEEP = '__keep'
const CLEAR = '__clear'
let nextKey = 1

const same = (a: string | null, b: string | null) => (a ?? '').localeCompare(b ?? '', undefined, { sensitivity: 'accent' }) === 0
const hostWord = (n: number) => (n === 1 ? '1 host' : `${n} hosts`)
const portOf = (text: string) => (text.trim() === '' ? null : Number(text))

/** How many hosts a tag change changes, in words (1.x). */
function tagImpact(change: TagChange, hosts: Host[]): string | null {
  const name = change.name.trim()
  if (!name) {
    return null
  }
  const value = change.value.trim()
  const has = (h: Host, v: string | null) => h.tags.some((t) => same(t.name, name) && (v === null || same(t.value, v)))
  const count =
    change.action === 'add'
      ? hosts.filter((h) => !has(h, value)).length
      : change.action === 'set'
        ? hosts.filter((h) => !(h.tags.filter((t) => same(t.name, name)).length === 1 && has(h, value))).length
        : hosts.filter((h) => has(h, value || null)).length
  if (change.action === 'remove') {
    return count === 0 ? 'No selected host has it' : count === 1 ? '1 host has it' : `${count} hosts have it`
  }
  return count === 0 ? 'All selected hosts already have it' : `Changes ${hostWord(count)}`
}

function serviceImpact(change: ServiceChange, hosts: Host[], types: Map<string, ServiceTypeInfo>): string | null {
  if (!change.type) {
    return null
  }
  const port = portOf(change.port)
  const label = types.get(change.type)?.label ?? change.type
  if (change.action === 'add') {
    const count = hosts.filter((h) => !h.services.some((s) => s.type === change.type && s.port === port)).length
    return count === 0 ? `All selected hosts already have ${label}${port ? ` on port ${port}` : ''}` : `Adds it to ${hostWord(count)}`
  }
  const count = hosts.filter((h) => h.services.some((s) => s.type === change.type && (port === null || s.port === port))).length
  return count === 0 ? 'No selected host has it' : count === 1 ? '1 host has it' : `${count} hosts have it`
}

function credentialImpact(change: CredentialChange, hosts: Host[]): string | null {
  const username = change.username.trim()
  if (!username) {
    return null
  }
  const domain = change.domain.trim() || null
  const has = (h: Host) => h.credentials.some((c) => same(c.username, username) && (change.action === 'add' ? same(c.domain, domain) : domain === null || same(c.domain, domain)))
  const count = change.action === 'add' ? hosts.filter((h) => !has(h)).length : hosts.filter(has).length
  if (change.action === 'remove') {
    return count === 0 ? 'No selected host has it' : count === 1 ? '1 host has it' : `${count} hosts have it`
  }
  return count === 0 ? 'All selected hosts already have it' : `Adds it to ${hostWord(count)}`
}

/** Whether a host runs on another, however indirectly (or is it). */
function runsOn(hosts: Host[], id: string, hypervisorId: string): boolean {
  const seen = new Set<string>()
  for (let current: string | null = id; current && !seen.has(current); current = hosts.find((h) => h.id === current)?.hypervisorId ?? null) {
    if (current === hypervisorId) {
      return true
    }
    seen.add(current)
  }
  return false
}

/**
 * 1.x's bulk edit (docs/architecture.md §7.50), for the hosts picked in the tree: add, set or remove tags, set or clear
 * the host they run on, add or remove services, and add or remove credentials, all in one change.
 */
export function BulkEditPanel({
  hosts,
  allHosts,
  serviceTypes,
  onApplied,
  onClear,
  onScan,
}: {
  hosts: Host[]
  allHosts: Host[]
  serviceTypes: Map<string, ServiceTypeInfo>
  onApplied: (hosts: Host[]) => void
  onClear: () => void
  /** 1.x's bulk scan; null when no loaded plugin scans any of them. */
  onScan: (() => void) | null
}) {
  const [tags, setTags] = useState<TagChange[]>([])
  const [services, setServices] = useState<ServiceChange[]>([])
  const [credentials, setCredentials] = useState<CredentialChange[]>([])
  const [hypervisor, setHypervisor] = useState(KEEP)
  const [busy, setBusy] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  const manageable = hosts.filter((h) => h.canManage)
  const readOnly = hosts.length - manageable.length
  const types = [...serviceTypes.values()].sort((a, b) => a.label.localeCompare(b.label))
  // Not one of them, and not a host that runs on one of them.
  const hypervisors = allHosts.filter((h) => !hosts.some((picked) => runsOn(allHosts, h.id, picked.id)))
  const readyTags = tags.filter((t) => t.name.trim() !== '')
  const readyServices = services.filter((s) => s.type !== '')
  const readyCredentials = credentials.filter((c) => c.username.trim() !== '')
  const nothing = readyTags.length + readyServices.length + readyCredentials.length === 0 && hypervisor === KEEP

  async function apply(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setMessage(null)
    try {
      const changed = await api.post<Host[]>('/api/hosts/bulk', {
        hostIds: manageable.map((h) => h.id),
        removeTags: readyTags.filter((t) => t.action === 'remove').map((t) => ({ name: t.name.trim(), value: t.value.trim() || null })),
        setTags: readyTags.filter((t) => t.action === 'set').map((t) => ({ name: t.name.trim(), value: t.value.trim() || null })),
        addTags: readyTags.filter((t) => t.action === 'add').map((t) => ({ name: t.name.trim(), value: t.value.trim() || null })),
        hypervisor: hypervisor === KEEP ? null : { hypervisorId: hypervisor === CLEAR ? null : hypervisor },
        removeServices: readyServices.filter((s) => s.action === 'remove').map((s) => ({ type: s.type, port: portOf(s.port) })),
        addServices: readyServices
          .filter((s) => s.action === 'add')
          .map((s) => {
            const type = serviceTypes.get(s.type)
            const settings = Object.fromEntries((type?.settings ?? []).filter((x) => x.kind === 'switch' && x.required).map((x) => [x.key, false]))
            return { type: s.type, label: null, port: portOf(s.port), address: null, settings, disabledTools: [] }
          }),
        removeCredentials: readyCredentials.filter((c) => c.action === 'remove').map((c) => ({ username: c.username.trim(), domain: c.domain.trim() || null })),
        addCredentials: readyCredentials
          .filter((c) => c.action === 'add')
          .map((c) => ({ username: c.username.trim(), domain: c.domain.trim() || null, password: c.password || null, serviceIndexes: null })),
      })
      onApplied(changed)
      setTags([])
      setServices([])
      setCredentials([])
      setHypervisor(KEEP)
      setMessage({ ok: true, text: `Applied to ${hostWord(changed.length)}.` })
    } catch (error) {
      const errors = error instanceof ApiError ? (error.problem as { errors?: Record<string, string[]> } | undefined)?.errors : undefined
      setMessage({ ok: false, text: Object.values(errors ?? {})[0]?.[0] ?? (error instanceof ApiError ? error.message : 'The changes couldn’t be applied.') })
    } finally {
      setBusy(false)
    }
  }

  const updateTag = (key: number, change: Partial<TagChange>) => setTags((all) => all.map((t) => (t.key === key ? { ...t, ...change } : t)))
  const updateService = (key: number, change: Partial<ServiceChange>) => setServices((all) => all.map((s) => (s.key === key ? { ...s, ...change } : s)))
  const updateCredential = (key: number, change: Partial<CredentialChange>) => setCredentials((all) => all.map((c) => (c.key === key ? { ...c, ...change } : c)))

  return (
    <form className={panel.panel} onSubmit={(e) => void apply(e)} noValidate aria-label="Change the selected hosts">
      <header className={panel.header}>
        <div className={panel.heading}>
          <h1 className={panel.title}>{hosts.length} hosts selected</h1>
          <p className={panel.subtitle}>
            Changes here apply to every selected host{readOnly > 0 ? `; ${hostWord(readOnly)} you can’t change ${readOnly === 1 ? 'is' : 'are'} left out` : ''}. Ctrl/⌘+click a host to add or remove it, Shift+click to add a range, Escape to clear.
          </p>
        </div>
        <div className={panel.actions}>
          {onScan && (
            <Button variant="secondary" size="sm" onClick={onScan}>
              Scan…
            </Button>
          )}
          <Button variant="secondary" size="sm" onClick={onClear}>
            Clear selection
          </Button>
        </div>
      </header>

      <div className={panel.body}>
        {message && <Alert variant={message.ok ? 'info' : 'error'}>{message.text}</Alert>}

        <section className={panel.section} aria-labelledby="bulk-tags">
          <h2 id="bulk-tags" className={panel.sectionTitle}>
            Tags
          </h2>
          {tags.map((t) => (
            <div key={t.key} className={panel.bulkRow}>
              <select aria-label="Tag change" className={list.input} value={t.action} onChange={(e) => updateTag(t.key, { action: e.target.value as TagAction })}>
                <option value="add">Add</option>
                <option value="set">Set</option>
                <option value="remove">Remove</option>
              </select>
              <TextField label="Tag name" hideLabel placeholder="Name" value={t.name} onChange={(e) => updateTag(t.key, { name: e.target.value })} maxLength={64} />
              <TextField
                label="Tag value"
                hideLabel
                placeholder={t.action === 'remove' ? 'Any value' : 'Value (optional)'}
                value={t.value}
                onChange={(e) => updateTag(t.key, { value: e.target.value })}
                maxLength={255}
              />
              <Button variant="ghost" size="sm" onClick={() => setTags((all) => all.filter((x) => x.key !== t.key))} aria-label="Remove this tag change">
                Remove
              </Button>
              {tagImpact(t, manageable) && <p className={`${panel.fieldHint} ${panel.bulkEffect}`}>{tagImpact(t, manageable)}</p>}
            </div>
          ))}
          <p className={panel.fieldHint}>Set replaces every value of the name; remove without a value takes off every value.</p>
          <div>
            <Button variant="secondary" size="sm" onClick={() => setTags((all) => [...all, { key: nextKey++, action: 'add', name: '', value: '' }])}>
              Add a tag change
            </Button>
          </div>
        </section>

        <section className={panel.section} aria-labelledby="bulk-hypervisor">
          <h2 id="bulk-hypervisor" className={panel.sectionTitle}>
            Runs on
          </h2>
          <div className={panel.field}>
            <label htmlFor="bulk-runs-on" className="visually-hidden">
              Runs on
            </label>
            <select id="bulk-runs-on" className={list.input} value={hypervisor} onChange={(e) => setHypervisor(e.target.value)}>
              <option value={KEEP}>Leave as it is</option>
              <option value={CLEAR}>Unknown (clear it)</option>
              {hypervisors.map((h) => (
                <option key={h.id} value={h.id}>
                  {h.name}
                </option>
              ))}
            </select>
            <p className={panel.fieldHint}>Choosing a host marks the selected hosts as virtual machines that run on it.</p>
          </div>
        </section>

        <section className={panel.section} aria-labelledby="bulk-services">
          <h2 id="bulk-services" className={panel.sectionTitle}>
            Services
          </h2>
          {services.map((s) => (
            <div key={s.key} className={panel.bulkRow}>
              <select aria-label="Service change" className={list.input} value={s.action} onChange={(e) => updateService(s.key, { action: e.target.value as ServiceChange['action'] })}>
                <option value="add">Add</option>
                <option value="remove">Remove</option>
              </select>
              <select aria-label="Service type" className={list.input} value={s.type} onChange={(e) => updateService(s.key, { type: e.target.value })}>
                <option value="">Choose a type</option>
                {types.map((type) => (
                  <option key={type.id} value={type.id}>
                    {type.label}
                  </option>
                ))}
              </select>
              <TextField
                label="Port"
                hideLabel
                inputMode="numeric"
                placeholder={s.action === 'remove' ? 'Any port' : `Port (${serviceTypes.get(s.type)?.defaultPort ?? 'default'})`}
                value={s.port}
                onChange={(e) => updateService(s.key, { port: e.target.value })}
              />
              <Button variant="ghost" size="sm" onClick={() => setServices((all) => all.filter((x) => x.key !== s.key))} aria-label="Remove this service change">
                Remove
              </Button>
              {serviceImpact(s, manageable, serviceTypes) && <p className={`${panel.fieldHint} ${panel.bulkEffect}`}>{serviceImpact(s, manageable, serviceTypes)}</p>}
            </div>
          ))}
          <p className={panel.fieldHint}>A host with the type on that port already keeps its own. Added services get the type’s default settings.</p>
          <div>
            <Button variant="secondary" size="sm" onClick={() => setServices((all) => [...all, { key: nextKey++, action: 'add', type: '', port: '' }])}>
              Add a service change
            </Button>
          </div>
        </section>

        <section className={panel.section} aria-labelledby="bulk-credentials">
          <h2 id="bulk-credentials" className={panel.sectionTitle}>
            Credentials
          </h2>
          {credentials.map((c) => (
            <div key={c.key} className={panel.bulkRow}>
              <select aria-label="Credential change" className={list.input} value={c.action} onChange={(e) => updateCredential(c.key, { action: e.target.value as CredentialChange['action'] })}>
                <option value="add">Add</option>
                <option value="remove">Remove</option>
              </select>
              <TextField label="User name" hideLabel placeholder="User name" value={c.username} onChange={(e) => updateCredential(c.key, { username: e.target.value })} autoComplete="off" />
              <TextField label="Domain" hideLabel placeholder={c.action === 'remove' ? 'Any domain' : 'Domain (optional)'} value={c.domain} onChange={(e) => updateCredential(c.key, { domain: e.target.value })} autoComplete="off" />
              <Button variant="ghost" size="sm" onClick={() => setCredentials((all) => all.filter((x) => x.key !== c.key))} aria-label="Remove this credential change">
                Remove
              </Button>
              {c.action === 'add' && (
                <PasswordField className={panel.bulkEffect} label="Password" value={c.password} onChange={(e) => updateCredential(c.key, { password: e.target.value })} autoComplete="new-password" />
              )}
              {credentialImpact(c, manageable) && <p className={`${panel.fieldHint} ${panel.bulkEffect}`}>{credentialImpact(c, manageable)}</p>}
            </div>
          ))}
          <p className={panel.fieldHint}>An added credential is used with every service of the host. A host with the account already keeps its own.</p>
          <div>
            <Button variant="secondary" size="sm" onClick={() => setCredentials((all) => [...all, { key: nextKey++, action: 'add', username: '', domain: '', password: '' }])}>
              Add a credential change
            </Button>
          </div>
        </section>
      </div>

      <footer className={panel.footer}>
        <Button type="submit" disabled={busy || nothing || manageable.length === 0}>
          {busy ? 'Applying…' : `Apply to ${hostWord(manageable.length)}`}
        </Button>
      </footer>
    </form>
  )
}
