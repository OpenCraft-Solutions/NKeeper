import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import type { Host } from './types'

/** Users and their grants, for administrators (docs/architecture.md §6.1, §7.2, §7.14). */

type Role = 'administrator' | 'user'
type Level = 'use' | 'manage'

interface User {
  id: string
  userName: string
  email: string | null
  role: Role
  isDisabled: boolean
  mustChangePassword: boolean
  isLockedOut: boolean
  createdAtUtc: string
}

interface Grant {
  id: string
  hostId: string | null
  hostName: string | null
  tagName: string | null
  tagValue: string | null
  level: Level
}

/** A grant as the editor holds it: on one host, or on a tag (with an optional value). */
interface GrantDraft {
  key: number
  kind: 'host' | 'tag'
  hostId: string
  tagName: string
  tagValue: string
  level: Level
}

let keys = 0

/** The server's message, with any field errors after it. */
function describe(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError)) {
    return fallback
  }
  const fields = Object.values(error.problem?.errors ?? {}).flat()
  return fields.length > 0 ? fields.join(' ') : error.message
}

export function UsersView({ currentUserId }: { currentUserId: string }) {
  const [users, setUsers] = useState<User[] | null>(null)
  const [hosts, setHosts] = useState<Host[]>([])
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | 'new' | null>(null)
  const [reloads, setReloads] = useState(0)
  const reload = useCallback(() => setReloads((n) => n + 1), [])

  useEffect(() => {
    let cancelled = false
    Promise.all([api.get<User[]>('/api/users'), api.get<Host[]>('/api/hosts')]).then(
      ([loadedUsers, loadedHosts]) => {
        if (!cancelled) {
          setUsers(loadedUsers)
          setHosts(loadedHosts)
          setError(null)
        }
      },
      (caught: unknown) => {
        if (!cancelled) {
          setError(describe(caught, 'The users could not be loaded.'))
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [reloads])

  const user = users?.find((u) => u.id === selected) ?? null
  return (
    <div className="nk-workspace" aria-label="Users">
      <aside className="nk-sidebar">
        <div className="nk-pad">
          <button type="button" className="nk-primary" onClick={() => setSelected('new')}>
            New user
          </button>
        </div>
        {error && (
          <p className="nk-alert nk-pad" role="alert">
            {error}
          </p>
        )}
        <nav className="nk-tree" aria-label="Users">
          <ul>
            {(users ?? []).map((u) => (
              <li key={u.id} className={u.id === selected ? 'nk-selected' : undefined}>
                <button type="button" className="nk-tree-host" aria-current={u.id === selected ? 'true' : undefined} onClick={() => setSelected(u.id)}>
                  <span className="nk-tree-name">{u.userName}</span>
                  <span className="nk-tree-address">
                    {[u.role === 'administrator' ? 'Administrator' : 'User', u.isDisabled && 'disabled', u.isLockedOut && 'locked out'].filter(Boolean).join(', ')}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </nav>
      </aside>
      <section className="nk-main nk-scroll">
        {selected === 'new' ? (
          <CreateUser
            onCreated={(created) => {
              reload()
              setSelected(created.id)
            }}
            onCancel={() => setSelected(null)}
          />
        ) : user ? (
          <UserPanel key={user.id} user={user} hosts={hosts} isSelf={user.id === currentUserId} onChanged={reload} onDeleted={() => { setSelected(null); reload() }} />
        ) : (
          <p className="nk-muted nk-pad">{users ? 'Select a user, or create one.' : 'Loading users…'}</p>
        )}
      </section>
    </div>
  )
}

function CreateUser({ onCreated, onCancel }: { onCreated: (user: User) => void; onCancel: () => void }) {
  const [userName, setUserName] = useState('')
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<Role>('user')
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [error, setError] = useState<string | null>(null)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    try {
      onCreated(await api.post<User>('/api/users', { userName, email, role, password, mustChangePassword: mustChange }))
    } catch (caught) {
      setError(describe(caught, 'The user could not be created.'))
    }
  }

  return (
    <form className="nk-form nk-editor" onSubmit={(e) => void submit(e)} aria-label="New user">
      <h2>New user</h2>
      {error && (
        <p className="nk-alert" role="alert">
          {error}
        </p>
      )}
      <div className="nk-row">
        <label>
          User name
          <input autoComplete="off" value={userName} onChange={(e) => setUserName(e.target.value)} required />
        </label>
        <label>
          Email
          <input type="email" autoComplete="off" value={email} onChange={(e) => setEmail(e.target.value)} required />
        </label>
        <label>
          Role
          <select value={role} onChange={(e) => setRole(e.target.value as Role)}>
            <option value="user">User: sees the hosts granted to them</option>
            <option value="administrator">Administrator: sees every host, manages users</option>
          </select>
        </label>
      </div>
      <label>
        Initial password
        <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
      </label>
      <label className="nk-check">
        <input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} /> They must change it when they first sign in
      </label>
      <div className="nk-actions">
        <button type="button" onClick={onCancel}>
          Cancel
        </button>
        <button type="submit" className="nk-primary">
          Create user
        </button>
      </div>
    </form>
  )
}

function UserPanel({ user, hosts, isSelf, onChanged, onDeleted }: { user: User; hosts: Host[]; isSelf: boolean; onChanged: () => void; onDeleted: () => void }) {
  const [email, setEmail] = useState(user.email ?? '')
  const [role, setRole] = useState<Role>(user.role)
  const [disabled, setDisabled] = useState(user.isDisabled)
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [confirmDelete, setConfirmDelete] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  async function run(action: () => Promise<unknown>, done: string, failed: string) {
    setMessage(null)
    try {
      await action()
      setMessage({ ok: true, text: done })
      return true
    } catch (caught) {
      setMessage({ ok: false, text: describe(caught, failed) })
      return false
    }
  }

  async function save(event: FormEvent) {
    event.preventDefault()
    if (await run(() => api.put(`/api/users/${user.id}`, { email, role, isDisabled: disabled }), 'Saved.', 'The user could not be saved.')) {
      onChanged()
    }
  }

  async function resetPassword(event: FormEvent) {
    event.preventDefault()
    if (await run(() => api.post(`/api/users/${user.id}/password`, { password, mustChangePassword: mustChange }), 'The password was reset, and their sessions ended.', 'The password could not be reset.')) {
      setPassword('')
      onChanged()
    }
  }

  return (
    <article className="nk-details" aria-label={user.userName}>
      <header className="nk-details-header">
        <div>
          <h2>{user.userName}</h2>
          <p className="nk-muted">
            {[user.mustChangePassword && 'must change their password', user.isLockedOut && 'locked out after failed sign-ins', isSelf && 'you'].filter(Boolean).join(' · ') || ' '}
          </p>
        </div>
        {!isSelf && (
          <div className="nk-actions">
            {confirmDelete ? (
              <>
                <button type="button" className="nk-danger" onClick={() => void run(() => api.delete(`/api/users/${user.id}`), 'Deleted.', 'The user could not be deleted.').then((ok) => ok && onDeleted())}>
                  Delete {user.userName}
                </button>
                <button type="button" onClick={() => setConfirmDelete(false)}>
                  Keep them
                </button>
              </>
            ) : (
              <button type="button" onClick={() => setConfirmDelete(true)}>
                Delete
              </button>
            )}
          </div>
        )}
      </header>
      {message && (
        <p className={message.ok ? 'nk-muted' : 'nk-alert'} role={message.ok ? 'status' : 'alert'}>
          {message.text}
        </p>
      )}

      <form className="nk-form" onSubmit={(e) => void save(e)} aria-label="Account">
        <h3>Account</h3>
        <div className="nk-row">
          <label>
            Email
            <input type="email" value={email} onChange={(e) => setEmail(e.target.value)} required />
          </label>
          <label>
            Role
            <select value={role} disabled={isSelf} onChange={(e) => setRole(e.target.value as Role)}>
              <option value="user">User</option>
              <option value="administrator">Administrator</option>
            </select>
          </label>
        </div>
        {!isSelf && (
          <label className="nk-check">
            <input type="checkbox" checked={disabled} onChange={(e) => setDisabled(e.target.checked)} /> Disabled: they can’t sign in, and their sessions end
          </label>
        )}
        <div className="nk-actions">
          <button type="submit">Save</button>
        </div>
      </form>

      {!isSelf && (
        <form className="nk-form" onSubmit={(e) => void resetPassword(e)} aria-label="Reset password">
          <h3>Reset password</h3>
          <label>
            New password
            <input type="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </label>
          <label className="nk-check">
            <input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} /> They must change it when they next sign in
          </label>
          <div className="nk-actions">
            <button type="submit">Reset password</button>
          </div>
        </form>
      )}

      <h3>Grants</h3>
      {user.role === 'administrator' ? (
        <p className="nk-muted">Administrators see and manage every host, so grants don’t apply to them.</p>
      ) : (
        <GrantsEditor userId={user.id} hosts={hosts} />
      )}
    </article>
  )
}

function GrantsEditor({ userId, hosts }: { userId: string; hosts: Host[] }) {
  const [grants, setGrants] = useState<GrantDraft[] | null>(null)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  useEffect(() => {
    let cancelled = false
    api.get<Grant[]>(`/api/users/${userId}/grants`).then(
      (loaded) => {
        if (!cancelled) {
          setGrants(
            loaded.map((g) => ({
              key: ++keys,
              kind: g.hostId ? 'host' : 'tag',
              hostId: g.hostId ?? '',
              tagName: g.tagName ?? '',
              tagValue: g.tagValue ?? '',
              level: g.level,
            })),
          )
        }
      },
      (caught: unknown) => {
        if (!cancelled) {
          setMessage({ ok: false, text: describe(caught, 'The grants could not be loaded.') })
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [userId])

  if (!grants) {
    return message ? <p className="nk-alert">{message.text}</p> : <p className="nk-muted">Loading grants…</p>
  }

  const update = (key: number, change: Partial<GrantDraft>) => setGrants(grants.map((g) => (g.key === key ? { ...g, ...change } : g)))

  async function save(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    try {
      await api.put(
        `/api/users/${userId}/grants`,
        grants!.map((g) =>
          g.kind === 'host' ? { hostId: g.hostId, level: g.level } : { tagName: g.tagName.trim(), tagValue: g.tagValue.trim() || null, level: g.level },
        ),
      )
      setMessage({ ok: true, text: 'Grants saved. Sessions they’re no longer allowed have ended.' })
    } catch (caught) {
      setMessage({ ok: false, text: describe(caught, 'The grants could not be saved.') })
    }
  }

  return (
    <form className="nk-form" onSubmit={(e) => void save(e)} aria-label="Grants">
      {message && (
        <p className={message.ok ? 'nk-muted' : 'nk-alert'} role={message.ok ? 'status' : 'alert'}>
          {message.text}
        </p>
      )}
      {grants.length === 0 && <p className="nk-muted">No grants: they see no hosts.</p>}
      {grants.map((grant, i) => (
        <fieldset key={grant.key} className="nk-item" aria-label={`Grant ${i + 1}`}>
          <div className="nk-row">
            <label>
              On
              <select value={grant.kind} onChange={(e) => update(grant.key, { kind: e.target.value as 'host' | 'tag' })}>
                <option value="host">One host</option>
                <option value="tag">Hosts with a tag</option>
              </select>
            </label>
            {grant.kind === 'host' ? (
              <label>
                Host
                <select value={grant.hostId} onChange={(e) => update(grant.key, { hostId: e.target.value })} required>
                  <option value="">Choose a host</option>
                  {hosts.map((h) => (
                    <option key={h.id} value={h.id}>
                      {h.name}
                    </option>
                  ))}
                </select>
              </label>
            ) : (
              <>
                <label>
                  Tag name
                  <input value={grant.tagName} onChange={(e) => update(grant.key, { tagName: e.target.value })} required />
                </label>
                <label>
                  Value (optional)
                  <input value={grant.tagValue} onChange={(e) => update(grant.key, { tagValue: e.target.value })} />
                </label>
              </>
            )}
            <label>
              Level
              <select value={grant.level} onChange={(e) => update(grant.key, { level: e.target.value as Level })}>
                <option value="use">Use: see and connect</option>
                <option value="manage">Manage: also change and delete</option>
              </select>
            </label>
          </div>
          <button type="button" className="nk-remove" onClick={() => setGrants(grants.filter((g) => g.key !== grant.key))}>
            Remove grant
          </button>
        </fieldset>
      ))}
      <div className="nk-actions">
        <button type="button" onClick={() => setGrants([...grants, { key: ++keys, kind: 'host', hostId: '', tagName: '', tagValue: '', level: 'use' }])}>
          Add grant
        </button>
        <button type="submit" className="nk-primary">
          Save grants
        </button>
      </div>
    </form>
  )
}
