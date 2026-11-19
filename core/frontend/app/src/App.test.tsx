import { act, render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useEffect, useState } from 'react'
import { api, definePlugin, type SessionViewProps } from '@nkeeper/sdk'
import { describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { loadPluginFrontends } from './plugins'
import { ADMIN, bodyOf, credential, FakeSocket, fakeApi, host, problem, service, status, terminalPlugin } from './test/fixtures'

/** Plugin administration with nothing in it: the catalog on, root's helper set up, no keys trusted from the page. */
const ADMIN_PLUGINS = {
  enabled: true,
  canRequest: true,
  canTrust: false,
  refreshedAt: '2026-11-18T12:00:00Z',
  catalogs: [{ url: 'https://catalog.test/catalog.json', official: true, error: null, warnings: [] }],
  plugins: [],
  conflicts: [],
  switches: {},
  keys: [],
  pending: [],
  results: [],
}

describe('loadPluginFrontends', () => {
  it('imports and activates a loaded plugin', async () => {
    const activate = vi.fn()
    const importer = vi.fn(async () => ({ default: definePlugin({ id: 'hello', activate }) }))

    const states = await loadPluginFrontends([status()], importer)

    expect(importer).toHaveBeenCalledWith('/plugins/hello/1.0.0/aaaaaaaaaaaa/index.js')
    expect(activate).toHaveBeenCalledWith(expect.objectContaining({ id: 'hello', version: '1.0.0' }))
    expect(states.get('hello')?.kind).toBe('active')
  })

  it('never imports plugins that are not loaded', async () => {
    const importer = vi.fn()

    const states = await loadPluginFrontends([status({ state: 'refused', reasonCode: 'hash_mismatch' })], importer)

    expect(importer).not.toHaveBeenCalled()
    expect(states.get('hello')).toEqual({ kind: 'none' })
  })

  it.each([
    ['another origin', 'https://evil.example/plugins/hello/x/index.js'],
    ['another plugin’s path', '/plugins/other/1.0.0/aaaaaaaaaaaa/index.js'],
    ['a non-plugin path', '/assets/index.js'],
  ])('refuses an entry URL on %s', async (_, url) => {
    const importer = vi.fn()

    const states = await loadPluginFrontends([status({ frontendEntry: url })], importer)

    expect(importer).not.toHaveBeenCalled()
    expect(states.get('hello')?.kind).toBe('failed')
  })

  it.each([
    ['no definition', { default: { notAPlugin: true } }],
    ['another plugin’s ID', { default: definePlugin({ id: 'other' }) }],
  ])('fails a module with %s, and only that plugin', async (_, module) => {
    const importer = vi.fn(async (url: string) => (url.includes('/hello/') ? module : { default: definePlugin({ id: 'second' }) }))

    const states = await loadPluginFrontends(
      [status(), status({ id: 'second', frontendEntry: '/plugins/second/1.0.0/aaaaaaaaaaaa/index.js' })],
      importer,
    )

    expect(states.get('hello')?.kind).toBe('failed')
    expect(states.get('second')?.kind).toBe('active')
  })

  it('activates a dependency first, and gives a dependant only what its dependencies export', async () => {
    const used: unknown[] = []
    const modules: Record<string, unknown> = {
      core: definePlugin({ id: 'core', exports: { answer: 42 } }),
      engine: definePlugin({
        id: 'engine',
        activate(context) {
          used.push(context.use('core').answer)
          expect(() => context.use('other')).toThrow(/doesn't depend on 'other'/)
        },
      }),
      other: definePlugin({ id: 'other' }),
    }
    const importer = vi.fn(async (url: string) => ({ default: modules[url.split('/')[2] ?? ''] }))
    const entry = (id: string) => `/plugins/${id}/1.0.0/aaaaaaaaaaaa/index.js`

    const states = await loadPluginFrontends(
      [
        status({ id: 'engine', frontendEntry: entry('engine'), dependsOn: ['core'] }),
        status({ id: 'core', frontendEntry: entry('core') }),
        status({ id: 'other', frontendEntry: entry('other') }),
      ],
      importer,
    )

    expect(importer.mock.calls.map(([url]) => url.split('/')[2])).toEqual(['core', 'engine', 'other'])
    expect(used).toEqual([42])
    expect(states.get('engine')?.kind).toBe('active')
  })

  it('fails a dependant whose dependency has no active frontend', async () => {
    const importer = vi.fn(async (url: string) =>
      url.includes('/core/')
        ? { default: { notAPlugin: true } }
        : { default: definePlugin({ id: 'engine', activate: (context) => void context.use('core') }) },
    )

    const states = await loadPluginFrontends(
      [
        status({ id: 'core', frontendEntry: '/plugins/core/1.0.0/aaaaaaaaaaaa/index.js' }),
        status({ id: 'engine', frontendEntry: '/plugins/engine/1.0.0/aaaaaaaaaaaa/index.js', dependsOn: ['core'] }),
      ],
      importer,
    )

    expect(states.get('engine')).toEqual({ kind: 'failed', reason: "'core', which 'engine' depends on, has no active frontend." })
  })

  it('fails a plugin whose import or activation throws', async () => {
    const importer = vi.fn(async () => ({ default: definePlugin({ id: 'hello', activate: () => { throw new Error('boom') } }) }))

    const states = await loadPluginFrontends([status()], importer)

    expect(states.get('hello')).toEqual({ kind: 'failed', reason: 'boom' })
  })
})

/** A plugin view that connects when it mounts, cancelling when it goes, and says how it went. */
function EchoView({ connect, host }: SessionViewProps) {
  const [state, setState] = useState('opening')
  useEffect(() => {
    const opening = new AbortController()
    connect({ columns: 80 }, opening.signal).then(
      () => setState('connected'),
      (error: { code?: string }) => setState(`failed: ${error.code}`),
    )
    return () => opening.abort()
  }, [connect])
  return <p>{`${host.name}: ${state}`}</p>
}

const termImporter = vi.fn(async () => ({ default: definePlugin({ id: 'term', sessionViews: { term: EchoView } }) }))

/** Selects a host in the tree and opens its Connect menu from the details, as 1.x did. */
async function openConnect(user: ReturnType<typeof userEvent.setup>, hostName: string) {
  await user.click(await screen.findByRole('button', { name: new RegExp(`^${hostName},`) }))
  await user.click(within(screen.getByRole('article', { name: hostName })).getByRole('button', { name: 'Connect' }))
}

/** Chooses an action's entry: the action and service, then the credential in its submenu. */
async function choose(user: ReturnType<typeof userEvent.setup>, group: string, entry: string) {
  await user.click(screen.getByRole('menuitem', { name: group }))
  await user.click(await screen.findByRole('menuitem', { name: entry }))
}

describe('App', () => {
  it('asks a signed-out user to sign in, then shows the workspace', async () => {
    let signedIn = false
    const fetch = fakeApi({
      'GET /api/auth/me': () => (signedIn ? Response.json(ADMIN) : problem(401)),
      'POST /api/auth/login': () => {
        signedIn = true
        return Response.json(ADMIN)
      },
      'GET /api/plugins': [],
      'GET /api/hosts': [host()],
      'GET /api/health': [],
    })
    const user = userEvent.setup()

    render(<App />)
    await user.type(await screen.findByLabelText('Username or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'Admin-Passw0rd-1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('button', { name: /^web01,/ })).toBeInTheDocument()
    expect(screen.getByText('admin')).toBeInTheDocument()
    expect(bodyOf(fetch, 'POST /api/auth/login')).toEqual({ userNameOrEmail: 'admin', password: 'Admin-Passw0rd-1', rememberMe: false })
  })

  it('shows why a sign-in failed and keeps the form', async () => {
    fakeApi({
      'GET /api/auth/me': () => problem(401),
      'POST /api/auth/login': () => problem(401, { detail: 'The user name or password is incorrect.' }),
    })
    const user = userEvent.setup()

    render(<App />)
    await user.type(await screen.findByLabelText('Username or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'wrong-password-1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The user name or password is incorrect.')
    expect(screen.getByLabelText('Password')).toHaveValue('')
  })

  it('stays signed in, and says so, when signing out fails', async () => {
    fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [],
      'GET /api/hosts': [],
      'GET /api/health': [],
      'POST /api/auth/logout': () => problem(503, { detail: 'The server is unavailable.' }),
    })
    const user = userEvent.setup()

    render(<App />)
    // Sign out is in the user's menu, as in 1.x.
    await user.click(await screen.findByRole('button', { name: 'admin' }))
    await user.click(screen.getByRole('menuitem', { name: 'Sign out' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The server is unavailable.')
    expect(screen.queryByRole('form', { name: 'Sign in' })).not.toBeInTheDocument()
  })

  it('keeps the sign-in form when the session ends while plugins are still loading', async () => {
    fakeApi({ 'GET /api/auth/me': ADMIN, 'GET /api/plugins': [status()] })
    // The plugin's activation calls the API and gets a 401: the session ended meanwhile.
    const importer = vi.fn(async () => ({
      default: definePlugin({ id: 'hello', activate: async () => { await api.get('/api/p/hello/ping').catch(() => undefined) } }),
    }))
    vi.mocked(fetch).mockImplementation(async (url) =>
      url === '/api/auth/me' ? Response.json(ADMIN) : url === '/api/plugins' ? Response.json([status()]) : problem(401),
    )

    render(<App importer={importer} />)

    expect(await screen.findByRole('form', { name: 'Sign in' })).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(screen.getByRole('form', { name: 'Sign in' })).toBeInTheDocument()
  })

  it('lists the plugins and their frontend state, for an administrator', async () => {
    fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [
        status({ credentialRelease: 'Its driver keeps the password.' }),
        status({ id: 'broken', state: 'refused', reasonCode: 'hash_mismatch', builtIn: false, frontendEntry: null }),
      ],
      'GET /api/hosts': [],
      'GET /api/health': [],
    })
    const user = userEvent.setup()

    render(<App importer={vi.fn(async () => ({ default: definePlugin({ id: 'hello' }) }))} />)
    await user.click(await screen.findByRole('button', { name: 'Settings' }))

    expect(screen.getByText('hello (on by default)')).toBeInTheDocument()
    expect(screen.getByText('Can see passwords: Its driver keeps the password.')).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.getByText('Refused (hash_mismatch)')).toBeInTheDocument()
  })

  it('shows the plugin catalog’s updates and new plugins, and asks root’s helper to install one', async () => {
    const candidate = (id: string, version: string) => ({ id, name: id.toUpperCase(), version, packageSha256: 'a'.repeat(64), keyId: 'k', builtIn: true, dependsOn: [] as string[] })
    const listing = (status: object, catalog = 'https://catalog.test/catalog.json') => ({ catalog, official: catalog === 'https://catalog.test/catalog.json', status })
    const admin = {
      ...ADMIN_PLUGINS,
      plugins: [
        listing({ id: 'ssh', repository: 'o/ssh', summary: null, installedVersion: '1.0.0', available: candidate('ssh', '1.1.0'), reasonCode: null, message: null }),
        listing({ id: 'kvm', repository: 'o/kvm', summary: 'KVM guests', installedVersion: null, available: { ...candidate('kvm', '1.0.0'), dependsOn: ['virt-core'] }, reasonCode: null, message: null }),
        listing({ id: 'zabbix', repository: 'o/zabbix', summary: null, installedVersion: null, available: null, reasonCode: 'sdk_incompatible', message: 'Needs SDK 2.' }),
        listing({ id: 'com.example.netbox', repository: 'example/netbox', summary: null, installedVersion: null, available: candidate('com.example.netbox', '2.0.0'), reasonCode: null, message: null }, 'https://example.com/catalog.json'),
      ],
    }
    const fetch = fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [status()],
      'GET /api/hosts': [],
      'GET /api/health': [],
      'GET /api/plugins/catalog': admin,
      'POST /api/plugins/catalog/requests': { ...admin, pending: ['kvm'] },
    })
    const user = userEvent.setup()

    render(<App importer={vi.fn(async () => ({ default: definePlugin({ id: 'hello' }) }))} />)
    await user.click(await screen.findByRole('button', { name: 'Settings' }))

    const updates = await screen.findByRole('table', { name: 'Updates' })
    expect(within(updates).getByText('1.0.0 → 1.1.0')).toBeInTheDocument()
    const available = screen.getByRole('table', { name: 'Available' })
    expect(within(available).getByText('KVM guests')).toBeInTheDocument()
    expect(within(available).getByText('https://example.com/catalog.json')).toBeInTheDocument()
    expect(screen.getByText('zabbix: Needs SDK 2. (sdk_incompatible)')).toBeInTheDocument()

    await user.click(within(available).getAllByRole('button', { name: 'Install' })[0]!)
    expect(screen.getByText(/with virt-core if missing/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Install and restart' }))

    expect(bodyOf(fetch, 'POST /api/plugins/catalog/requests')).toEqual({ id: 'kvm' })
    expect(await within(screen.getByRole('table', { name: 'Available' })).findByRole('button', { name: 'Waiting…' })).toBeDisabled()
  })

  it('installs several ticked plugins with one request, so NKeeper restarts once', async () => {
    const candidate = (id: string, version: string) => ({ id, name: id.toUpperCase(), version, packageSha256: 'a'.repeat(64), keyId: 'k', builtIn: true, dependsOn: [] as string[] })
    const listing = (status: object) => ({ catalog: 'https://catalog.test/catalog.json', official: true, status })
    const admin = {
      ...ADMIN_PLUGINS,
      plugins: [
        listing({ id: 'ssh', repository: 'o/ssh', summary: null, installedVersion: '1.0.0', available: candidate('ssh', '1.1.0'), reasonCode: null, message: null }),
        listing({ id: 'kvm', repository: 'o/kvm', summary: null, installedVersion: null, available: candidate('kvm', '1.0.0'), reasonCode: null, message: null }),
        listing({ id: 'zabbix', repository: 'o/zabbix', summary: null, installedVersion: null, available: candidate('zabbix', '1.0.0'), reasonCode: null, message: null }),
      ],
    }
    const fetch = fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [status()],
      'GET /api/hosts': [],
      'GET /api/health': [],
      'GET /api/plugins/catalog': admin,
      'POST /api/plugins/catalog/requests': { ...admin, pending: ['ssh', 'zabbix'] },
    })
    const user = userEvent.setup()

    render(<App importer={vi.fn(async () => ({ default: definePlugin({ id: 'hello' }) }))} />)
    await user.click(await screen.findByRole('button', { name: 'Settings' }))
    expect(await screen.findByRole('button', { name: 'Install selected' })).toBeDisabled()
    await user.click(screen.getByRole('checkbox', { name: 'Select ssh' }))
    await user.click(screen.getByRole('checkbox', { name: 'Select zabbix' }))
    await user.click(screen.getByRole('button', { name: 'Install 2 selected' }))
    expect(screen.getByText(/ssh 1\.1\.0, zabbix 1\.0\.0\? NKeeper restarts once/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Install 2 and restart' }))

    expect(bodyOf(fetch, 'POST /api/plugins/catalog/requests')).toEqual({ ids: ['ssh', 'zabbix'] })
    expect(await screen.findByRole('checkbox', { name: 'Select zabbix' })).toBeDisabled()
  })

  it('turns a plugin off with the plugins that need it, at the next restart, and offers the restart', async () => {
    const admin = { ...ADMIN_PLUGINS, switches: { 'db-core': true, mssql: true } }
    const fetch = fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [status({ id: 'db-core', frontendEntry: null }), status({ id: 'mssql', frontendEntry: null, dependsOn: ['db-core'] })],
      'GET /api/hosts': [],
      'GET /api/health': [],
      'GET /api/plugins/catalog': admin,
      'POST /api/plugins/db-core/disable': { changed: ['mssql', 'db-core'], catalog: { ...admin, switches: { 'db-core': false, mssql: false } } },
    })
    const user = userEvent.setup()

    render(<App />)
    await user.click(await screen.findByRole('button', { name: 'Settings' }))
    const row = (await screen.findByText('db-core (on by default)')).closest('tr')!
    await user.click(within(row).getByRole('button', { name: 'Turn off' }))

    expect(screen.getByText(/mssql needs it and turn off too/)).toBeInTheDocument()
    await user.click(screen.getByRole('button', { name: 'Turn db-core off' }))

    expect(fetch).toHaveBeenCalledWith('/api/plugins/db-core/disable', expect.objectContaining({ method: 'POST' }))
    expect(await screen.findAllByText('Off at the next restart')).toHaveLength(2)
    expect(screen.getByRole('button', { name: 'Restart now (ends every open session)' })).toBeInTheDocument()
  })

  it('hides plugins and creating hosts from other users', async () => {
    fakeApi({ 'GET /api/auth/me': { ...ADMIN, roles: [] }, 'GET /api/plugins': [], 'GET /api/hosts': [host({ canManage: false })], 'GET /api/health': [] })

    render(<App />)

    expect(await screen.findByRole('button', { name: /^web01,/ })).toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'Settings' })).not.toBeInTheDocument()
    expect(screen.queryByRole('button', { name: 'New host' })).not.toBeInTheDocument()
  })
})

describe('the users page', () => {
  it('creates a user, and grants them a host and a tag group', async () => {
    const created = { id: 'u2', userName: 'bob', email: 'bob@example.com', role: 'user', isDisabled: false, mustChangePassword: true, isLockedOut: false, createdAtUtc: '2026-01-01T00:00:00Z' }
    let users: object[] = [{ ...created, id: 'u1', userName: 'admin', email: 'admin@example.com', role: 'administrator', mustChangePassword: false }]
    const fetch = fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [],
      'GET /api/hosts': [host({ id: 'h1', name: 'web01' })],
      'GET /api/health': [],
      'GET /api/users': () => Response.json(users),
      'POST /api/users': () => {
        users = [...users, created]
        return Response.json(created, { status: 201 })
      },
      'GET /api/users/u2/grants': [],
      'PUT /api/users/u2/grants': () => new Response(null, { status: 204 }),
    })
    const user = userEvent.setup()

    render(<App />)
    await user.click(await screen.findByRole('button', { name: 'Users' }))
    await user.click(screen.getByRole('button', { name: 'New user' }))
    const dialog = screen.getByRole('dialog', { name: 'New user' })
    await user.type(within(dialog).getByLabelText('Username'), 'bob')
    await user.type(within(dialog).getByLabelText('Email'), 'bob@example.com')
    await user.type(within(dialog).getByLabelText('Password'), 'Bob-Passw0rd-123')
    await user.click(within(dialog).getByRole('button', { name: 'Create user' }))

    const panel = await screen.findByRole('article', { name: 'bob' })
    expect(bodyOf(fetch, 'POST /api/users')).toEqual({ userName: 'bob', email: 'bob@example.com', role: 'user', password: 'Bob-Passw0rd-123', mustChangePassword: true })
    await user.click(await within(panel).findByRole('button', { name: 'Add host' }))
    await user.selectOptions(within(panel).getByLabelText('Host for grant 1'), 'h1')
    await user.click(within(panel).getByRole('button', { name: 'Add group' }))
    await user.type(within(panel).getByLabelText('Tag name for grant 2'), 'env')
    await user.type(within(panel).getByLabelText('Tag value for grant 2'), 'prod')
    await user.selectOptions(within(panel).getByLabelText('Access level for grant 2'), 'manage')
    await user.click(within(panel).getByRole('button', { name: 'Save access' }))

    expect(await within(panel).findByRole('status')).toHaveTextContent('Access saved.')
    expect(bodyOf(fetch, 'PUT /api/users/u2/grants')).toEqual([
      { hostId: 'h1', level: 'use' },
      { tagName: 'env', tagValue: 'prod', level: 'manage' },
    ])
  })
})

describe('a required password change', () => {
  it('comes before anything else', async () => {
    let changed = false
    const fetch = fakeApi({
      'GET /api/auth/me': () => Response.json({ ...ADMIN, mustChangePassword: !changed }),
      'POST /api/account/password': () => {
        changed = true
        return new Response(null, { status: 204 })
      },
      'GET /api/plugins': [],
      'GET /api/hosts': [],
      'GET /api/health': [],
    })
    const user = userEvent.setup()

    render(<App />)
    const form = await screen.findByRole('form', { name: 'Change your password' })
    expect(fetch.mock.calls.some(([url]) => url === '/api/plugins')).toBe(false)
    await user.type(within(form).getByLabelText('Current password'), 'Old-Passw0rd-1')
    await user.type(within(form).getByLabelText('New password'), 'New-Passw0rd-1')
    await user.type(within(form).getByLabelText('Confirm the new password'), 'New-Passw0rd-2')
    await user.click(within(form).getByRole('button', { name: 'Change password' }))
    expect(within(form).getByRole('alert')).toHaveTextContent('The new passwords don’t match.')

    await user.clear(within(form).getByLabelText('Confirm the new password'))
    await user.type(within(form).getByLabelText('Confirm the new password'), 'New-Passw0rd-1')
    await user.click(within(form).getByRole('button', { name: 'Change password' }))

    expect(await screen.findByText('No hosts yet.')).toBeInTheDocument()
    expect(bodyOf(fetch, 'POST /api/account/password')).toEqual({ currentPassword: 'Old-Passw0rd-1', newPassword: 'New-Passw0rd-1' })
  })
})

describe('the workspace', () => {
  const hosts = [
    host({ id: 'h1', name: 'web01', tags: [{ name: 'env', value: 'prod' }], services: [service({ id: 's1' })], credentials: [credential()] }),
    host({ id: 'h2', name: 'db01', ipAddress: '192.0.2.20', tags: [{ name: 'env', value: 'test' }] }),
  ]

  function signedIn(routes: Record<string, object | ((init: RequestInit | undefined) => Response)> = {}) {
    return fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [terminalPlugin()],
      'GET /api/hosts': hosts,
      'GET /api/health': [{ hostId: 'h1', serviceId: 's1', state: 'down', latencyMs: 3, checkedAtUtc: '2026-01-01T00:00:00Z', reason: 'refused' }],
      ...routes,
    })
  }

  it('searches and groups the tree, and shows a host’s details and health', async () => {
    signedIn()
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    const sidebar = await screen.findByRole('complementary', { name: 'Host inventory' })
    await user.type(within(sidebar).getByLabelText('Search hosts'), 'env=test')
    expect(within(sidebar).queryByRole('button', { name: /^web01,/ })).not.toBeInTheDocument()
    await user.clear(within(sidebar).getByLabelText('Search hosts'))

    await user.selectOptions(within(sidebar).getByLabelText('Group by'), 'env')
    expect(within(sidebar).getByRole('button', { name: /prod/, expanded: true })).toBeInTheDocument()
    expect(within(sidebar).getByRole('button', { name: /test/, expanded: true })).toBeInTheDocument()

    await user.click(within(sidebar).getByRole('button', { name: /^web01,/ }))
    const details = screen.getByRole('article', { name: 'web01' })
    expect(within(details).getByText('Down · Connection refused')).toBeInTheDocument()
    expect(within(sidebar).getByRole('img', { name: 'Not answering' })).toBeInTheDocument()
  })

  it('shows a health page of what’s down and which certificates expire, and goes back to a host from it', async () => {
    const soon = new Date(Date.now() + 5 * 86_400_000 + 3_600_000).toISOString()
    signedIn({
      'GET /api/hosts': [...hosts, host({ id: 'h3', name: 'portal', services: [service({ id: 's3', label: 'Portal' })] })],
      'GET /api/health': [
        { hostId: 'h1', serviceId: 's1', state: 'down', latencyMs: 3, checkedAtUtc: '2026-01-01T00:00:00Z', reason: 'refused' },
        { hostId: 'h3', serviceId: 's3', state: 'up', latencyMs: 4, checkedAtUtc: '2026-01-01T00:01:00Z', reason: null, certificateSubject: 'CN=portal', certificateExpiresAtUtc: soon },
      ],
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: 'Reports' }))
    expect(await screen.findByText(/1 of 2 services up · 1 down · 1 certificate expiring within 30 days/)).toBeInTheDocument()
    const down = screen.getByRole('region', { name: 'Down' })
    expect(within(down).getByRole('cell', { name: 'Connection refused' })).toBeInTheDocument()
    const certificates = screen.getByRole('region', { name: 'Certificates' })
    expect(within(certificates).getByRole('cell', { name: 'CN=portal' })).toBeInTheDocument()
    expect(within(certificates).getByText(/\(in 5 days\)/)).toHaveAttribute('data-warning', 'true')

    await user.click(within(certificates).getByRole('button', { name: 'portal' }))
    const details = await screen.findByRole('article', { name: 'portal' })
    expect(within(details).getByText('Up · 4 ms')).toBeInTheDocument()
    expect(within(details).getByText(/Certificate expires in 5 days/)).toBeInTheDocument()
  })

  it('opens a session in its own tab with a typed credential, asking about the server key', async () => {
    vi.stubGlobal('WebSocket', FakeSocket)
    const fetch = signedIn({ 'POST /api/hosts/h1/services/s1/credential-prompt': { reference: 'ref-1', expiresAtUtc: '2026-01-01T00:02:00Z' } })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await openConnect(user, 'web01')
    await user.click(screen.getByRole('menuitem', { name: 'Terminal · Term :22' }))
    expect(await screen.findByRole('menuitem', { name: 'root' })).toBeInTheDocument()
    await user.click(screen.getByRole('menuitem', { name: 'Enter credentials…' }))

    const typed = await screen.findByRole('dialog', { name: 'Enter credentials' })
    await user.type(within(typed).getByLabelText('Username'), 'ops')
    await user.type(within(typed).getByLabelText('Password'), 'secret-1')
    await user.click(within(typed).getByRole('button', { name: 'Connect' }))

    await waitFor(() => expect(FakeSocket.last).not.toBeNull())
    expect(bodyOf(fetch, 'POST /api/hosts/h1/services/s1/credential-prompt')).toEqual({ username: 'ops', domain: null, password: 'secret-1', privateKey: null })
    const socket = FakeSocket.last!
    expect(socket.url).toMatch(/\/api\/sessions\/term$/)
    act(() => socket.open())
    expect(socket.sent[0]).toEqual({ type: 'start', hostId: 'h1', serviceId: 's1', promptReference: 'ref-1', options: { columns: 80 } })

    act(() => socket.receiveJson({ type: 'hostKey', hostId: 'h1', serviceId: 's1', algorithm: 'ssh-ed25519', fingerprint: 'abc123' }))
    const key = await screen.findByRole('dialog', { name: 'Trust this host key?' })
    expect(within(key).getByText('SHA256:abc123')).toBeInTheDocument()
    await user.click(within(key).getByRole('button', { name: 'Trust and connect' }))
    await waitFor(() => expect(socket.sent.at(-1)).toEqual({ type: 'hostKeyDecision', trust: true }))

    act(() => socket.receiveJson({ type: 'connected' }))
    expect(await screen.findByText('web01: connected')).toBeInTheDocument()
    expect(screen.getByRole('tab', { name: 'web01 · Terminal' })).toHaveAttribute('aria-selected', 'true')

    // Closing the tab ends the session.
    await user.click(screen.getByRole('button', { name: 'Close web01 · Terminal' }))
    expect(screen.queryByRole('tab', { name: 'web01 · Terminal' })).not.toBeInTheDocument()
  })

  it('opens a host’s menu on right-click, its first entry on double-click, and says when there’s nothing to connect with', async () => {
    vi.stubGlobal('WebSocket', FakeSocket)
    signedIn()
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    const tree = await screen.findByRole('list', { name: 'Hosts' })
    await user.pointer({ keys: '[MouseRight]', target: within(tree).getByRole('button', { name: /^web01,/ }) })
    expect(screen.getByRole('menu', { name: 'Actions for web01' })).toBeInTheDocument()
    await user.keyboard('{Escape}')
    expect(screen.queryByRole('menu')).not.toBeInTheDocument()

    // Double-click: the first saved credential, root.
    await user.dblClick(within(tree).getByRole('button', { name: /^web01,/ }))
    await waitFor(() => expect(FakeSocket.last).not.toBeNull())
    act(() => FakeSocket.last!.open())
    expect(FakeSocket.last!.sent[0]).toMatchObject({ type: 'start', hostId: 'h1', serviceId: 's1', credentialId: 'c1' })
    expect(screen.getByRole('tab', { name: 'web01 · Terminal' })).toBeInTheDocument()

    // db01 has no services.
    await user.click(within(tree).getByRole('button', { name: /^db01,/ }))
    expect(screen.getByRole('note')).toHaveTextContent('There’s nothing to connect with yet: add a service, such as SSH, with Edit.')
  })

  it('runs a plugin’s run action and shows its answer', async () => {
    const fetch = signedIn({ 'POST /api/p/term/hosts/h1/reboot': { message: 'Rebooting web01.' } })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await openConnect(user, 'web01')
    await user.click(screen.getByRole('menuitem', { name: 'Reboot · Term :22' }))

    expect(await screen.findByRole('status')).toHaveTextContent('Rebooting web01.')
    expect(bodyOf(fetch, 'POST /api/p/term/hosts/h1/reboot')).toEqual({ serviceId: 's1' })
    expect(screen.queryByRole('tab', { name: /Reboot/ })).not.toBeInTheDocument()
  })

  it('keeps sessions running behind the plugin list, and withdraws a dialog when its tab closes', async () => {
    vi.stubGlobal('WebSocket', FakeSocket)
    signedIn()
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await openConnect(user, 'web01')
    await choose(user, 'Terminal · Term :22', 'root')
    await waitFor(() => expect(FakeSocket.last).not.toBeNull())
    const socket = FakeSocket.last!
    act(() => socket.open())
    act(() => socket.receiveJson({ type: 'connected' }))
    expect(await screen.findByText('web01: connected')).toBeInTheDocument()

    await user.click(screen.getByRole('button', { name: 'Settings' }))
    await user.click(screen.getByRole('button', { name: 'Hosts' }))
    expect(screen.getByText('web01: connected')).toBeInTheDocument()
    expect(socket.readyState).toBe(FakeSocket.OPEN)

    // A second tab, waiting on a typed credential, is closed before it's answered.
    await user.click(screen.getByRole('tab', { name: /Details/ }))
    await user.click(within(screen.getByRole('article', { name: 'web01' })).getByRole('button', { name: 'Connect' }))
    await choose(user, 'Terminal · Term :22', 'Enter credentials…')
    expect(await screen.findByRole('dialog', { name: 'Enter credentials' })).toBeInTheDocument()
    await user.click(screen.getAllByRole('button', { name: 'Close web01 · Terminal' }).at(-1)!)
    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Enter credentials' })).not.toBeInTheDocument())
  })

  it('catches a port that isn’t a number before sending', async () => {
    const fetch = signedIn()
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: 'New host' }))
    const form = screen.getByRole('form', { name: 'New host' })
    await user.type(within(form).getByLabelText('Name'), 'new01')
    await user.click(within(form).getByRole('button', { name: 'Add service' }))
    await user.type(within(form).getByLabelText('Port for service 1'), 'abc')
    await user.selectOptions(within(form).getByLabelText('Mode for service 1'), 'fast')
    await user.click(within(form).getByRole('button', { name: 'Create host' }))

    expect(await within(form).findByText('Enter a port from 1 to 65535.')).toBeInTheDocument()
    expect(fetch.mock.calls.some(([url, init]) => url === '/api/hosts' && init?.method === 'POST')).toBe(false)
  })

  it('creates a host with a service whose settings form comes from its type’s schema, and shows field errors', async () => {
    let attempts = 0
    const created = host({ id: 'h9', name: 'new01' })
    const fetch = signedIn({
      'POST /api/hosts': () =>
        ++attempts === 1
          ? problem(400, { title: 'One or more validation errors occurred.', errors: { 'services[0].port': ['Use a port from 1 to 65535.'] } })
          : Response.json(created, { status: 201 }),
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: 'New host' }))
    const form = screen.getByRole('form', { name: 'New host' })
    await user.type(within(form).getByLabelText('Name'), 'new01')
    await user.type(within(form).getByLabelText('IP address'), '192.0.2.99')
    await user.click(within(form).getByRole('button', { name: 'Add service' }))
    await user.type(within(form).getByLabelText('Port for service 1'), '99999')
    await user.type(within(form).getByLabelText('Shell for service 1'), '/bin/zsh')
    await user.selectOptions(within(form).getByLabelText('Mode for service 1'), 'safe')
    await user.type(within(form).getByLabelText('Retries for service 1'), '3')
    await user.click(within(form).getByLabelText('Compress for service 1'))
    await user.click(within(form).getByRole('button', { name: 'Add credential' }))
    await user.type(within(form).getByLabelText('Username'), 'root')
    await user.click(within(form).getByRole('button', { name: 'Create host' }))

    expect(await within(form).findByText('Use a port from 1 to 65535.')).toBeInTheDocument()
    expect(bodyOf(fetch, 'POST /api/hosts')).toEqual({
      name: 'new01',
      hostname: null,
      ipAddress: '192.0.2.99',
      description: null,
      macAddress: null,
      cpuCores: null,
      memoryMiB: null,
      serialNumber: null,
      isVirtualMachine: false,
      tags: [],
      services: [{ id: null, type: 'term', label: null, port: 99999, address: null, settings: { shell: '/bin/zsh', mode: 'safe', retries: 3, compress: true }, disabledTools: [] }],
      credentials: [
        { id: null, label: null, username: 'root', domain: null, notes: null, password: null, clearPassword: false, privateKey: null, clearPrivateKey: false, serviceIndexes: null },
      ],
      jump: null,
    })

    await user.clear(within(form).getByLabelText('Port for service 1'))
    await user.click(within(form).getByRole('button', { name: 'Create host' }))
    expect(await screen.findByRole('article', { name: 'new01' })).toBeInTheDocument()
    // It's checked at once, rather than at the next background round.
    await waitFor(() => expect(fetch.mock.calls.some(([url, init]) => url === '/api/hosts/h9/health' && init?.method === 'POST')).toBe(true))
  })

  it('turns a service’s tools off and on, when its type has more than one', async () => {
    const fetch = signedIn({
      'GET /api/plugins': [
        terminalPlugin({
          sessionTypes: [
            { id: 'term', label: 'Terminal', serviceTypes: ['term'], supportsJumpHost: true },
            { id: 'term-files', label: 'Files', serviceTypes: ['term'], supportsJumpHost: true },
          ],
        }),
      ],
      'POST /api/hosts': () => Response.json(host({ id: 'h9', name: 'new01' }), { status: 201 }),
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: 'New host' }))
    const form = screen.getByRole('form', { name: 'New host' })
    await user.type(within(form).getByLabelText('Name'), 'new01')
    await user.click(within(form).getByRole('button', { name: 'Add service' }))
    await user.selectOptions(within(form).getByLabelText('Mode for service 1'), 'safe')
    expect(within(form).getByLabelText('Terminal for service 1')).toBeChecked()
    await user.click(within(form).getByLabelText('Files for service 1'))
    await user.click(within(form).getByLabelText('Terminal for service 1'))
    await user.click(within(form).getByLabelText('Terminal for service 1'))
    await user.click(within(form).getByRole('button', { name: 'Create host' }))

    await waitFor(() => expect(bodyOf(fetch, 'POST /api/hosts')).toMatchObject({ services: [{ type: 'term', disabledTools: ['term-files'] }] }))
  })

  it('scans a host and applies the findings the user picks, from the version it opened with', async () => {
    const plugin = terminalPlugin()
    const fetch = signedIn({
      'GET /api/plugins': [
        {
          ...plugin,
          hostActions: [...(plugin.hostActions ?? []), { id: 'term.scan', label: 'Scan', sessionType: null, endpoint: 'scan', serviceTypes: ['term'], credential: 'required', scan: true }],
        },
      ],
      'POST /api/p/term/hosts/h1/scan': { hostname: 'web01.lab.example.com', macAddress: 'aa:bb:cc:dd:ee:ff', operatingSystem: 'Debian 12', notes: ['No serial number: not physical hardware.'] },
      'PUT /api/hosts/h1': (init) => Response.json({ ...hosts[0], ...(JSON.parse(String(init?.body)) as object), version: 2 }),
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: /^web01,/ }))
    await user.click(screen.getByRole('button', { name: 'Scan' }))
    const dialog = await screen.findByRole('dialog', { name: 'Scan web01' })
    await user.click(within(dialog).getByRole('button', { name: 'Scan' }))

    expect(await within(dialog).findByText('No serial number: not physical hardware.')).toBeInTheDocument()
    expect(bodyOf(fetch, 'POST /api/p/term/hosts/h1/scan')).toMatchObject({ serviceId: 's1', credentialId: 'c1' })
    // The hostname would replace one, so it starts unticked; the MAC address and the os tag fill gaps.
    expect(within(dialog).getByRole('checkbox', { name: /Hostname/ })).not.toBeChecked()
    await user.click(within(dialog).getByRole('checkbox', { name: /Operating system/ }))
    await user.click(within(dialog).getByRole('button', { name: 'Apply' }))

    await waitFor(() => expect(screen.queryByRole('dialog', { name: 'Scan web01' })).not.toBeInTheDocument())
    expect(bodyOf(fetch, 'PUT /api/hosts/h1')).toMatchObject({ hostname: 'web01.example.com', macAddress: 'AA:BB:CC:DD:EE:FF', tags: [{ name: 'env', value: 'prod' }], version: 1 })
  })

  it('offers a host-wide scanner once for a host without services, posting no service (SDK 1.14)', async () => {
    const plugin = terminalPlugin()
    const fetch = signedIn({
      'GET /api/plugins': [
        {
          ...plugin,
          hostActions: [...(plugin.hostActions ?? []), { id: 'term.external', label: 'From the server', sessionType: null, endpoint: 'external', serviceTypes: [], credential: 'none', scan: true }],
        },
      ],
      'POST /api/p/term/hosts/h2/external': { ports: [{ port: 22, name: 'SSH' }], notes: ['Probed 36 ports.'] },
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: /^db01,/ }))
    await user.click(screen.getByRole('button', { name: 'Scan' }))
    const dialog = await screen.findByRole('dialog', { name: 'Scan db01' })
    await user.click(within(dialog).getByRole('button', { name: 'Scan' }))

    expect(await within(dialog).findByText('Probed 36 ports.')).toBeInTheDocument()
    expect(bodyOf(fetch, 'POST /api/p/term/hosts/h2/external')).toEqual({})
  })

  it('opens a plugin’s inventory tool from above the tree, and reloads the hosts it asks to (SDK 1.14)', async () => {
    let loads = 0
    signedIn({ 'GET /api/hosts': () => Response.json(loads++ === 0 ? hosts : [...hosts, host({ id: 'h3', name: 'found01' })]) })
    const importer = vi.fn(async () => ({
      default: definePlugin({
        id: 'term',
        sessionViews: { term: EchoView },
        inventoryTools: {
          'network-scan': {
            label: 'Scan network',
            title: 'Find the machines on a subnet',
            view: ({ close, hostsChanged }) => (
              <button
                type="button"
                onClick={() => {
                  hostsChanged()
                  close()
                }}
              >
                Add found01
              </button>
            ),
          },
        },
      }),
    }))
    const user = userEvent.setup()

    render(<App importer={importer} />)
    await user.click(await screen.findByRole('button', { name: 'Scan network' }))
    const dialog = await screen.findByRole('dialog', { name: 'Scan network' })
    await user.click(within(dialog).getByRole('button', { name: 'Add found01' }))

    expect(await screen.findByRole('button', { name: /^found01,/ })).toBeInTheDocument()
    expect(screen.queryByRole('dialog', { name: 'Scan network' })).not.toBeInTheDocument()
  })

  it('lists a plugin’s reports after Health, and hides an administrators’ one from others (SDK 1.14)', async () => {
    signedIn({ 'GET /api/auth/me': { ...ADMIN, roles: [] } })
    const importer = vi.fn(async () => ({
      default: definePlugin({
        id: 'term',
        sessionViews: { term: EchoView },
        reports: {
          'ip-addresses': { label: 'IP addresses', description: 'By subnet', view: ({ showHost }) => <button onClick={() => showHost('h2')}>db01 at .20</button> },
          secret: { label: 'Licences', description: 'Administrators only', view: () => null, administrators: true },
        },
      }),
    }))
    const user = userEvent.setup()

    render(<App importer={importer} />)
    await user.click(await screen.findByRole('button', { name: 'Reports' }))
    const list = screen.getByRole('navigation', { name: 'Reports' })
    expect(within(list).queryByRole('button', { name: /Licences/ })).not.toBeInTheDocument()
    await user.click(within(list).getByRole('button', { name: /IP addresses/ }))
    await user.click(screen.getByRole('button', { name: 'db01 at .20' }))

    expect(await screen.findByRole('article', { name: 'db01' })).toBeInTheDocument()
  })

  it('sends the version an edit started from, and offers to reload after a conflict', async () => {
    let loads = 0
    const fetch = signedIn({
      'GET /api/hosts': () => {
        loads++
        return Response.json(hosts)
      },
      'PUT /api/hosts/h2': () => problem(409, { code: 'version_conflict', detail: 'Changed.' }),
    })
    const user = userEvent.setup()

    render(<App importer={termImporter} />)
    await user.click(await screen.findByRole('button', { name: /^db01,/ }))
    await user.click(screen.getByRole('button', { name: 'Edit' }))
    await user.click(screen.getByRole('button', { name: 'Save changes' }))

    expect(await screen.findByText('Someone else changed this host since you started editing it.')).toBeInTheDocument()
    expect(bodyOf(fetch, 'PUT /api/hosts/h2')).toMatchObject({ name: 'db01', version: 1 })
    await user.click(screen.getByRole('button', { name: 'Reload the host' }))
    await waitFor(() => expect(loads).toBe(2))
  })
})

describe('plugin panels (SDK 1.13)', () => {
  const zabbix = status({ id: 'zabbix', frontendEntry: '/plugins/zabbix/1.0.0/aaaaaaaaaaaa/index.js' })
  const server = host({ id: 'h9', name: 'zbx01', services: [service({ id: 's9', type: 'zabbix' }), service({ id: 's8', type: 'term' })], credentials: [credential({ id: 'c9', label: 'API token', serviceIndexes: [0] }), credential({ id: 'c8', serviceIndexes: [1] })] })

  it('shows a plugin’s host panels in a host’s details, and one that throws says so alone', async () => {
    fakeApi({ 'GET /api/auth/me': ADMIN, 'GET /api/plugins': [zabbix], 'GET /api/hosts': [host({ canManage: false })], 'GET /api/health': [] })
    const Broken = () => {
      throw new Error('no data')
    }
    const importer = vi.fn(async () => ({
      default: definePlugin({
        id: 'zabbix',
        hostPanels: { Zabbix: ({ host: h, canManage }) => <p>{`${h.name} at ${h.ipAddress}, ${canManage ? 'manage' : 'use'}`}</p>, Broken },
      }),
    }))
    vi.spyOn(console, 'error').mockImplementation(() => undefined)
    const user = userEvent.setup()

    render(<App importer={importer} />)
    const sidebar = await screen.findByRole('complementary', { name: 'Host inventory' })
    await user.click(await within(sidebar).findByRole('button', { name: /^web01,/ }))

    const details = screen.getByRole('article', { name: 'web01' })
    expect(within(within(details).getByRole('region', { name: 'Zabbix' })).getByText('web01 at 192.0.2.10, use')).toBeInTheDocument()
    expect(within(within(details).getByRole('region', { name: 'Broken' })).getByRole('alert')).toHaveTextContent('The zabbix plugin couldn’t draw this: no data')
  })

  it('binds an integration with core’s pickers, above the plugin’s settings panel', async () => {
    let bound = false
    const fetch = fakeApi({
      'GET /api/auth/me': ADMIN,
      'GET /api/plugins': [zabbix],
      'GET /api/hosts': [server, host()],
      'GET /api/health': [],
      'GET /api/plugins/catalog': ADMIN_PLUGINS,
      'GET /api/plugins/integrations': () =>
        Response.json([
          {
            pluginId: 'zabbix',
            name: 'server',
            label: 'Zabbix server',
            serviceTypes: ['zabbix'],
            binding: bound ? { hostId: 'h9', hostName: 'zbx01', serviceId: 's9', serviceLabel: 'zabbix', credentialId: 'c9', credentialLabel: 'API token', setAtUtc: '2026-11-18T12:00:00Z' } : null,
          },
        ]),
      'PUT /api/plugins/zabbix/integrations/server': () => {
        bound = true
        return new Response(null, { status: 204 })
      },
    })
    const importer = vi.fn(async () => ({
      default: definePlugin({ id: 'zabbix', settingsPanel: ({ integrations }) => <p>{integrations.every((i) => i.bound) ? 'Ready' : 'Bind the server first'}</p> }),
    }))
    const user = userEvent.setup()

    render(<App importer={importer} />)
    await user.click(await screen.findByRole('button', { name: 'Settings' }))
    const section = await screen.findByRole('region', { name: 'zabbix settings' })
    expect(within(section).getByText('Bind the server first')).toBeInTheDocument()

    // Only hosts with a service of the integration's type, and only credentials used with the chosen service.
    expect(within(section).getAllByRole('option').map((o) => o.textContent)).not.toContain('web01')
    await user.selectOptions(within(section).getByLabelText('Host'), 'h9')
    await user.selectOptions(within(section).getByLabelText('Service'), 's9')
    expect(within(within(section).getByLabelText('Credential')).getAllByRole('option').map((o) => o.textContent)).toEqual(['Choose a credential', 'API token'])
    await user.selectOptions(within(section).getByLabelText('Credential'), 'c9')
    await user.click(within(section).getByRole('button', { name: 'Save' }))

    expect(await within(section).findByText('Ready')).toBeInTheDocument()
    expect(bodyOf(fetch, 'PUT /api/plugins/zabbix/integrations/server')).toEqual({ hostId: 'h9', serviceId: 's9', credentialId: 'c9' })
    expect(within(section).getByText(/^zbx01 · zabbix · API token, set/)).toBeInTheDocument()
  })
})
