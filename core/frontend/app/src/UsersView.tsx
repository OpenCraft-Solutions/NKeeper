import { useCallback, useEffect, useId, useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { hostAddress, tagNames } from './hosts'
import type { Host } from './types'
import { Alert } from './ui/Alert'
import { Badge } from './ui/Badge'
import { Button } from './ui/Button'
import { Checkbox } from './ui/Checkbox'
import { DeleteButton } from './ui/DeleteButton'
import { Dialog } from './ui/Dialog'
import dialogs from './ui/UserDialogs.module.css'
import grantStyles from './ui/GrantsEditor.module.css'
import panel from './ui/HostPanel.module.css'
import { PasswordField } from './ui/PasswordField'
import roleStyles from './ui/RoleSelect.module.css'
import { Spinner } from './ui/Spinner'
import { TextField } from './ui/TextField'
import styles from './ui/UsersPage.module.css'

/** Users and their grants, for administrators (docs/architecture.md §6.1, §7.2, §7.14), as 1.x's Users page drew them. */

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

const ROLES: { value: Role; label: string; description: string }[] = [
  { value: 'user', label: 'User', description: 'Sees only the hosts and groups granted to them.' },
  { value: 'administrator', label: 'Administrator', description: 'Sees and manages every host, and manages users.' },
]

const LEVELS: Record<Level, { label: string; description: string }> = {
  use: { label: 'Use', description: 'See and connect' },
  manage: { label: 'Manage', description: 'Also edit and delete' },
}

const dateFormat = new Intl.DateTimeFormat(undefined, { dateStyle: 'medium' })
let keys = 0

/** The server's message, with any field errors after it. */
function describe(error: unknown, fallback: string): string {
  if (!(error instanceof ApiError)) {
    return fallback
  }
  const fields = Object.values(error.problem?.errors ?? {}).flat()
  return fields.length > 0 ? fields.join(' ') : error.message
}

/** Whether a grant covers a host now: the host itself, or a tag it has (any value, or the one named). */
function grantMatches(grant: GrantDraft, host: Host): boolean {
  if (grant.kind === 'host') {
    return grant.hostId === host.id
  }
  const name = grant.tagName.trim().toLowerCase()
  const value = grant.tagValue.trim().toLowerCase()
  return name !== '' && host.tags.some((t) => t.name.toLowerCase() === name && (!value || (t.value ?? '').toLowerCase() === value))
}

/** A random password that meets the default policy (12+ characters, upper, lower, digit), as 1.x made them. */
function generatePassword(length = 16): string {
  const required = ['ABCDEFGHJKLMNPQRSTUVWXYZ', 'abcdefghijkmnopqrstuvwxyz', '23456789']
  const all = required.join('') + '-_.!@#'
  const random = (max: number) => {
    // Rejection sampling avoids modulo bias.
    const limit = Math.floor(0x100000000 / max) * max
    const buffer = new Uint32Array(1)
    do {
      crypto.getRandomValues(buffer)
    } while ((buffer[0] ?? limit) >= limit)
    return (buffer[0] ?? 0) % max
  }
  const chars = [...required.map((set) => set[random(set.length)] ?? ''), ...Array.from({ length: length - required.length }, () => all[random(all.length)] ?? '')]
  for (let i = chars.length - 1; i > 0; i--) {
    const j = random(i + 1)
    ;[chars[i], chars[j]] = [chars[j] ?? '', chars[i] ?? '']
  }
  return chars.join('')
}

export function UsersView({ currentUserId }: { currentUserId: string }) {
  const [users, setUsers] = useState<User[] | null>(null)
  const [hosts, setHosts] = useState<Host[]>([])
  const [error, setError] = useState<string | null>(null)
  const [selected, setSelected] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [search, setSearch] = useState('')
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
  const words = search.trim().toLowerCase()
  const visible = (users ?? []).filter((u) => !words || u.userName.toLowerCase().includes(words) || (u.email ?? '').toLowerCase().includes(words))

  return (
    <div className={`${styles.page} ${selected ? styles.detailOpen : ''}`}>
      <aside className={styles.sidebar} aria-label="Users">
        <div className={styles.sidebarHeader}>
          <h1 className={styles.title}>
            Users {users && <span className={styles.count}>{users.length}</span>}
          </h1>
          <Button size="sm" onClick={() => setCreating(true)}>
            New user
          </Button>
        </div>
        <div className={styles.searchBox}>
          <input className={styles.search} type="search" value={search} onChange={(e) => setSearch(e.target.value)} placeholder="Search username or email" aria-label="Search users" />
        </div>
        <div className={styles.listScroll}>
          {error && !users ? (
            <div className={styles.state}>
              <Alert>{error}</Alert>
            </div>
          ) : !users ? (
            <div className={styles.state}>
              <Spinner label="Loading users" />
            </div>
          ) : (
            <ul className={styles.list}>
              {visible.map((u) => (
                <li key={u.id}>
                  <button type="button" className={styles.row} aria-current={u.id === selected ? 'true' : undefined} onClick={() => setSelected(u.id)}>
                    <span className={styles.avatar} aria-hidden="true">
                      {u.userName.slice(0, 1).toUpperCase()}
                    </span>
                    <span className={styles.rowText}>
                      <span className={styles.rowName}>{u.userName}</span>
                      <span className={styles.rowEmail}>{u.email}</span>
                    </span>
                    <span className={styles.badges}>
                      {u.role === 'administrator' && <Badge variant="accent">Admin</Badge>}
                      {u.isDisabled && <Badge>Disabled</Badge>}
                      {u.isLockedOut && <Badge>Locked</Badge>}
                    </span>
                  </button>
                </li>
              ))}
              {visible.length === 0 && <li className={styles.state}>No users match “{search}”.</li>}
            </ul>
          )}
        </div>
      </aside>

      <main className={styles.main}>
        {selected && (
          <button type="button" className={styles.back} onClick={() => setSelected(null)}>
            ‹ All users
          </button>
        )}
        {user ? (
          <UserPanel
            key={user.id}
            user={user}
            hosts={hosts}
            isSelf={user.id === currentUserId}
            onChanged={reload}
            onDeleted={() => {
              setSelected(null)
              reload()
            }}
          />
        ) : (
          <div className={styles.empty}>
            <p className={styles.emptyTitle}>Select a user</p>
            <p>Choose a user to change their role, reset their password or decide which hosts and groups they can access.</p>
          </div>
        )}
      </main>

      {creating && (
        <NewUserDialog
          onClose={() => setCreating(false)}
          onCreated={(created) => {
            setCreating(false)
            reload()
            setSelected(created.id)
          }}
        />
      )}
    </div>
  )
}

/** Role as two clear choices rather than a bare dropdown, as 1.x asked. */
function RoleSelect({ value, onChange, disabled }: { value: Role; onChange: (role: Role) => void; disabled?: boolean }) {
  return (
    <fieldset className={roleStyles.fieldset} disabled={disabled}>
      <legend className={roleStyles.legend}>Role</legend>
      <div className={roleStyles.options}>
        {ROLES.map((role) => (
          <label key={role.value} className={`${roleStyles.option} ${value === role.value ? roleStyles.selected : ''}`}>
            <input type="radio" name="role" value={role.value} checked={value === role.value} onChange={() => onChange(role.value)} />
            <span>
              <span className={roleStyles.label}>{role.label}</span>
              <span className={roleStyles.description}>{role.description}</span>
            </span>
          </label>
        ))}
      </div>
    </fieldset>
  )
}

/** A password field with Generate and Copy, for passwords an administrator hands out. */
function TemporaryPasswordField({ value, onChange }: { value: string; onChange: (value: string) => void }) {
  const [copied, setCopied] = useState(false)
  return (
    <div className={dialogs.passwordRow}>
      <PasswordField
        className={dialogs.grow}
        label="Password"
        value={value}
        onChange={(e) => {
          setCopied(false)
          onChange(e.target.value)
        }}
        hint="At least 12 characters, with upper- and lower-case letters and a digit."
        autoComplete="new-password"
      />
      <div className={dialogs.passwordActions}>
        <Button
          variant="secondary"
          size="sm"
          onClick={() => {
            setCopied(false)
            onChange(generatePassword())
          }}
        >
          Generate
        </Button>
        <Button variant="ghost" size="sm" disabled={!value || !navigator.clipboard} onClick={() => void navigator.clipboard?.writeText(value).then(() => setCopied(true), () => {})}>
          {copied ? 'Copied' : 'Copy'}
        </Button>
      </div>
    </div>
  )
}

function NewUserDialog({ onClose, onCreated }: { onClose: () => void; onCreated: (user: User) => void }) {
  const [userName, setUserName] = useState('')
  const [email, setEmail] = useState('')
  const [role, setRole] = useState<Role>('user')
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setSaving(true)
    try {
      onCreated(await api.post<User>('/api/users', { userName, email, role, password, mustChangePassword: mustChange }))
    } catch (caught) {
      setError(describe(caught, 'The user could not be created.'))
      setSaving(false)
    }
  }

  return (
    <Dialog
      title="New user"
      description="Users sign in with their username or email. Give them the password below."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="submit" form="new-user" loading={saving}>
            Create user
          </Button>
        </>
      }
    >
      <form id="new-user" className={dialogs.form} onSubmit={(e) => void submit(e)} noValidate aria-label="New user">
        {error && <Alert>{error}</Alert>}
        <div className={dialogs.grid}>
          <TextField label="Username" value={userName} onChange={(e) => setUserName(e.target.value)} autoComplete="off" maxLength={64} data-autofocus />
          <TextField label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} autoComplete="off" maxLength={256} />
        </div>
        <RoleSelect value={role} onChange={setRole} />
        <TemporaryPasswordField value={password} onChange={setPassword} />
        <Checkbox label="Require a new password at first sign-in" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
      </form>
    </Dialog>
  )
}

function ResetPasswordDialog({ user, onClose, onDone }: { user: User; onClose: () => void; onDone: () => void }) {
  const [password, setPassword] = useState('')
  const [mustChange, setMustChange] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    if (!password) {
      setError('Enter or generate a password.')
      return
    }
    setSaving(true)
    try {
      await api.post(`/api/users/${user.id}/password`, { password, mustChangePassword: mustChange })
      onDone()
    } catch (caught) {
      setError(describe(caught, 'The password could not be reset.'))
      setSaving(false)
    }
  }

  return (
    <Dialog
      title={`Reset ${user.userName}’s password`}
      description="They’re signed out everywhere. Give them the new password."
      onClose={onClose}
      footer={
        <>
          <Button variant="secondary" onClick={onClose} disabled={saving}>
            Cancel
          </Button>
          <Button type="submit" form="reset-password" loading={saving}>
            Reset password
          </Button>
        </>
      }
    >
      <form id="reset-password" className={dialogs.form} onSubmit={(e) => void submit(e)} noValidate aria-label="Reset password">
        {error && <Alert>{error}</Alert>}
        <TemporaryPasswordField value={password} onChange={setPassword} />
        <Checkbox label="Require a new password at next sign-in" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
      </form>
    </Dialog>
  )
}

function UserPanel({ user, hosts, isSelf, onChanged, onDeleted }: { user: User; hosts: Host[]; isSelf: boolean; onChanged: () => void; onDeleted: () => void }) {
  const [email, setEmail] = useState(user.email ?? '')
  const [role, setRole] = useState<Role>(user.role)
  const [disabled, setDisabled] = useState(user.isDisabled)
  const [resetting, setResetting] = useState(false)
  const [deleting, setDeleting] = useState(false)
  const [saving, setSaving] = useState(false)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)

  async function save(event: FormEvent) {
    event.preventDefault()
    setMessage(null)
    setSaving(true)
    try {
      await api.put(`/api/users/${user.id}`, { email, role, isDisabled: disabled })
      setMessage({ ok: true, text: 'Account saved.' })
      onChanged()
    } catch (caught) {
      setMessage({ ok: false, text: describe(caught, 'The user could not be saved.') })
    } finally {
      setSaving(false)
    }
  }

  async function remove() {
    setMessage(null)
    setDeleting(true)
    try {
      await api.delete(`/api/users/${user.id}`)
      onDeleted()
    } catch (caught) {
      setMessage({ ok: false, text: describe(caught, 'The user could not be deleted.') })
      setDeleting(false)
    }
  }

  return (
    <article className={panel.panel} aria-labelledby="user-title" aria-label={user.userName}>
      <header className={panel.header}>
        <div className={panel.heading}>
          <h1 id="user-title" className={panel.title}>
            {user.userName}
          </h1>
          <p className={panel.subtitle}>
            {user.role === 'administrator' ? 'Administrator' : 'User'} · created {dateFormat.format(new Date(user.createdAtUtc))}
            {user.isDisabled ? ' · disabled' : ''}
            {user.isLockedOut ? ' · locked out after failed sign-ins' : ''}
            {user.mustChangePassword ? ' · must change password' : ''}
          </p>
        </div>
        <div className={panel.actions}>
          {isSelf ? <Badge variant="accent">You</Badge> : <DeleteButton question={`Delete ${user.userName}?`} onConfirm={() => void remove()} loading={deleting} />}
        </div>
      </header>

      <div className={panel.body}>
        {message && <Alert variant={message.ok ? 'info' : 'error'}>{message.text}</Alert>}

        <form className={panel.section} onSubmit={(e) => void save(e)} noValidate aria-labelledby="account-title">
          <h2 id="account-title" className={panel.sectionTitle}>
            Account
          </h2>
          <TextField label="Email" type="email" value={email} onChange={(e) => setEmail(e.target.value)} maxLength={256} />
          <RoleSelect value={role} onChange={setRole} disabled={isSelf} />
          <Checkbox label="Disable this account (blocks sign-in and ends all sessions)" checked={disabled} onChange={(e) => setDisabled(e.target.checked)} disabled={isSelf} />
          {isSelf && <p className={panel.muted}>You can’t change your own role or disable yourself.</p>}
          <div>
            <Button type="submit" loading={saving}>
              Save account
            </Button>
          </div>
        </form>

        <section className={panel.section} aria-labelledby="password-title">
          <h2 id="password-title" className={panel.sectionTitle}>
            Password
          </h2>
          <p className={panel.muted}>
            {user.isLockedOut ? 'The account is locked after too many failed sign-ins. Resetting the password unlocks it. ' : null}
            Set a new password and give it to the user; they can be asked to change it at their next sign-in.
          </p>
          <div>
            <Button variant="secondary" onClick={() => setResetting(true)}>
              Reset password…
            </Button>
          </div>
        </section>

        <section className={panel.section} aria-labelledby="access-title">
          <h2 id="access-title" className={panel.sectionTitle}>
            Host access
          </h2>
          {user.role === 'administrator' ? (
            <p className={panel.muted}>Administrators can use and manage every host.</p>
          ) : (
            <GrantsEditor userId={user.id} hosts={hosts} />
          )}
        </section>
      </div>

      {resetting && (
        <ResetPasswordDialog
          user={user}
          onClose={() => setResetting(false)}
          onDone={() => {
            setResetting(false)
            setMessage({ ok: true, text: `Password reset. ${user.userName} was signed out everywhere.` })
            onChanged()
          }}
        />
      )}
    </article>
  )
}

/** Which hosts a user may use or manage, as 1.x edited them: hosts, or groups by tag, each with how many hosts it covers now. */
function GrantsEditor({ userId, hosts }: { userId: string; hosts: Host[] }) {
  const id = useId()
  const namesList = `${id}-names`
  const [grants, setGrants] = useState<GrantDraft[] | null>(null)
  const [message, setMessage] = useState<{ ok: boolean; text: string } | null>(null)
  const [saving, setSaving] = useState(false)

  useEffect(() => {
    let cancelled = false
    api.get<Grant[]>(`/api/users/${userId}/grants`).then(
      (loaded) => {
        if (!cancelled) {
          setGrants(
            loaded.map((g) => ({ key: ++keys, kind: g.hostId ? 'host' : 'tag', hostId: g.hostId ?? '', tagName: g.tagName ?? '', tagValue: g.tagValue ?? '', level: g.level })),
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
    return message ? <Alert>{message.text}</Alert> : <Spinner label="Loading access" />
  }

  const sortedHosts = [...hosts].sort((a, b) => a.name.localeCompare(b.name, undefined, { sensitivity: 'base', numeric: true }))
  const covered = hosts.filter((h) => grants.some((g) => grantMatches(g, h)))
  const manageable = hosts.filter((h) => grants.some((g) => g.level === 'manage' && grantMatches(g, h)))
  const update = (key: number, change: Partial<GrantDraft>) => setGrants(grants.map((g) => (g.key === key ? { ...g, ...change } : g)))
  const add = (kind: 'host' | 'tag') => setGrants([...grants, { key: ++keys, kind, hostId: '', tagName: '', tagValue: '', level: 'use' }])

  async function save() {
    setMessage(null)
    setSaving(true)
    try {
      await api.put(
        `/api/users/${userId}/grants`,
        grants!.map((g) => (g.kind === 'host' ? { hostId: g.hostId, level: g.level } : { tagName: g.tagName.trim(), tagValue: g.tagValue.trim() || null, level: g.level })),
      )
      setMessage({ ok: true, text: 'Access saved. Sessions they’re no longer allowed have ended.' })
    } catch (caught) {
      setMessage({ ok: false, text: describe(caught, 'The access could not be saved.') })
    } finally {
      setSaving(false)
    }
  }

  return (
    <>
      <p className={panel.muted}>
        Grant groups of hosts by tag (e.g. <code>site: Datacenter A</code>, or just <code>site</code> for any value) or individual hosts. Group grants include hosts tagged
        later.
      </p>
      <div className={grantStyles.editor}>
        <datalist id={namesList}>
          {tagNames(hosts).map((name) => (
            <option key={name} value={name} />
          ))}
        </datalist>
        {grants.length === 0 && <p className={grantStyles.empty}>No access yet. This user will see an empty host tree.</p>}
        <ul className={grantStyles.list} aria-label="Access grants">
          {grants.map((grant, index) => {
            const position = `grant ${index + 1}`
            const matches = hosts.filter((h) => grantMatches(grant, h)).length
            return (
              <li key={grant.key} className={grantStyles.row}>
                <span className={grantStyles.kind}>{grant.kind === 'tag' ? 'Group' : 'Host'}</span>
                {grant.kind === 'tag' ? (
                  <div className={grantStyles.target}>
                    <input
                      className={grantStyles.input}
                      value={grant.tagName}
                      onChange={(e) => update(grant.key, { tagName: e.target.value.replace(/[:=]/g, '') })}
                      placeholder="Tag name, e.g. site"
                      list={namesList}
                      aria-label={`Tag name for ${position}`}
                      autoComplete="off"
                    />
                    <span className={grantStyles.separator}>:</span>
                    <input
                      className={grantStyles.input}
                      value={grant.tagValue}
                      onChange={(e) => update(grant.key, { tagValue: e.target.value })}
                      placeholder="any value"
                      aria-label={`Tag value for ${position}`}
                      autoComplete="off"
                    />
                  </div>
                ) : (
                  <select className={`${grantStyles.input} ${grantStyles.target}`} value={grant.hostId} onChange={(e) => update(grant.key, { hostId: e.target.value })} aria-label={`Host for ${position}`}>
                    <option value="">Choose a host…</option>
                    {sortedHosts.map((host) => (
                      <option key={host.id} value={host.id}>
                        {host.name} — {hostAddress(host)}
                      </option>
                    ))}
                  </select>
                )}
                <select className={grantStyles.input} value={grant.level} onChange={(e) => update(grant.key, { level: e.target.value as Level })} aria-label={`Access level for ${position}`}>
                  {(Object.entries(LEVELS) as [Level, (typeof LEVELS)[Level]][]).map(([level, info]) => (
                    <option key={level} value={level}>
                      {info.label} — {info.description}
                    </option>
                  ))}
                </select>
                <span className={grantStyles.matches}>{matches === 1 ? '1 host' : `${matches} hosts`}</span>
                <button type="button" className={grantStyles.remove} onClick={() => setGrants(grants.filter((g) => g.key !== grant.key))} aria-label={`Remove ${position}`}>
                  ×
                </button>
              </li>
            )
          })}
        </ul>
        <div className={grantStyles.footer}>
          <div className={grantStyles.actions}>
            <Button variant="secondary" size="sm" onClick={() => add('tag')}>
              Add group
            </Button>
            <Button variant="secondary" size="sm" onClick={() => add('host')}>
              Add host
            </Button>
          </div>
          <p className={grantStyles.summary} aria-live="polite">
            Can use {covered.length} of {hosts.length} hosts{manageable.length > 0 ? `, manage ${manageable.length}` : ''}.
          </p>
        </div>
      </div>
      {message && <Alert variant={message.ok ? 'info' : 'error'}>{message.text}</Alert>}
      <div>
        <Button onClick={() => void save()} loading={saving}>
          Save access
        </Button>
      </div>
    </>
  )
}
