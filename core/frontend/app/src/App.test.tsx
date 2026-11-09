import { render, screen } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { api, definePlugin } from '@nkeeper/sdk'
import { describe, expect, it, vi } from 'vitest'
import { App } from './App'
import { loadPluginFrontends, type PluginStatus } from './plugins'

function status(overrides: Partial<PluginStatus> = {}): PluginStatus {
  return {
    id: 'hello',
    version: '1.0.0',
    state: 'loaded',
    reasonCode: null,
    builtIn: true,
    packageSha256: 'a'.repeat(64),
    keyId: 'b'.repeat(32),
    frontendEntry: '/plugins/hello/1.0.0/aaaaaaaaaaaa/index.js',
    frontendEntrySha256: 'c'.repeat(64),
    ...overrides,
  }
}

describe('loadPluginFrontends', () => {
  it('imports and activates a loaded plugin', async () => {
    const activate = vi.fn()
    const importer = vi.fn(async () => ({ default: definePlugin({ id: 'hello', activate }) }))

    const states = await loadPluginFrontends([status()], importer)

    expect(importer).toHaveBeenCalledWith('/plugins/hello/1.0.0/aaaaaaaaaaaa/index.js')
    expect(activate).toHaveBeenCalledWith({ id: 'hello', version: '1.0.0' })
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

  it('fails a plugin whose import or activation throws', async () => {
    const importer = vi.fn(async () => ({ default: definePlugin({ id: 'hello', activate: () => { throw new Error('boom') } }) }))

    const states = await loadPluginFrontends([status()], importer)

    expect(states.get('hello')).toEqual({ kind: 'failed', reason: 'boom' })
  })
})

describe('App', () => {
  it('lists the plugins and their frontend state', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([status(), status({ id: 'broken', state: 'refused', reasonCode: 'hash_mismatch', builtIn: false, frontendEntry: null })])))
    const importer = vi.fn(async () => ({ default: definePlugin({ id: 'hello' }) }))

    render(<App importer={importer} />)

    expect(await screen.findByText('hello (built in)')).toBeInTheDocument()
    expect(screen.getByText('Active')).toBeInTheDocument()
    expect(screen.getByText('Refused (hash_mismatch)')).toBeInTheDocument()
  })

  it('asks a signed-out user to sign in, then lists the plugins', async () => {
    let signedIn = false
    const fetch = vi.fn(async (url: string, init?: RequestInit) => {
      if (url === '/api/auth/antiforgery') {
        return Response.json({ token: 'token-1', headerName: 'X-XSRF-TOKEN' })
      }
      if (url === '/api/auth/login' && init?.method === 'POST') {
        signedIn = true
        return Response.json({ id: 'u1', userName: 'admin', email: null, roles: ['Administrator'], mustChangePassword: false })
      }
      return signedIn
        ? Response.json([status({ frontendEntry: null })])
        : Response.json({ status: 401, title: 'Sign-in required' }, { status: 401, headers: { 'Content-Type': 'application/problem+json' } })
    })
    vi.stubGlobal('fetch', fetch)
    const user = userEvent.setup()

    render(<App />)
    await user.type(await screen.findByLabelText('User name or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'Admin-Passw0rd-1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByText('hello (built in)')).toBeInTheDocument()
    const login = fetch.mock.calls.find(([url]) => url === '/api/auth/login')!
    expect(JSON.parse(login[1]!.body as string)).toEqual({ userNameOrEmail: 'admin', password: 'Admin-Passw0rd-1', rememberMe: false })
    expect((login[1]!.headers as Record<string, string>)['X-XSRF-TOKEN']).toBe('token-1')
  })

  it('shows why a sign-in failed and keeps the form', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url === '/api/auth/antiforgery') {
        return Response.json({ token: 'token-1', headerName: 'X-XSRF-TOKEN' })
      }
      const detail = url === '/api/auth/login' ? 'The user name or password is incorrect.' : 'Sign in to continue.'
      return Response.json({ status: 401, detail }, { status: 401, headers: { 'Content-Type': 'application/problem+json' } })
    }))
    const user = userEvent.setup()

    render(<App />)
    await user.type(await screen.findByLabelText('User name or email'), 'admin')
    await user.type(screen.getByLabelText('Password'), 'wrong-password-1')
    await user.click(screen.getByRole('button', { name: 'Sign in' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The user name or password is incorrect.')
    expect(screen.getByLabelText('Password')).toHaveValue('')
  })

  it('stays signed in, and says so, when signing out fails', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) => {
      if (url === '/api/auth/antiforgery') {
        return Response.json({ token: 'token-1', headerName: 'X-XSRF-TOKEN' })
      }
      if (url === '/api/auth/logout') {
        return Response.json({ status: 503, detail: 'The server is unavailable.' }, { status: 503, headers: { 'Content-Type': 'application/problem+json' } })
      }
      return Response.json([status({ frontendEntry: null })])
    }))
    const user = userEvent.setup()

    render(<App />)
    await user.click(await screen.findByRole('button', { name: 'Sign out' }))

    expect(await screen.findByRole('alert')).toHaveTextContent('The server is unavailable.')
    expect(screen.getByText('hello (built in)')).toBeInTheDocument()
    expect(screen.queryByRole('form', { name: 'Sign in' })).not.toBeInTheDocument()
  })

  it('keeps the sign-in form when the session ends while plugins are still loading', async () => {
    vi.stubGlobal('fetch', vi.fn(async (url: string) =>
      url === '/api/plugins'
        ? Response.json([status()])
        : Response.json({ status: 401 }, { status: 401, headers: { 'Content-Type': 'application/problem+json' } }),
    ))
    // The plugin's activation calls the API and gets a 401: the session ended meanwhile.
    const importer = vi.fn(async () => ({
      default: definePlugin({ id: 'hello', activate: async () => { await api.get('/api/p/hello/ping').catch(() => undefined) } }),
    }))

    render(<App importer={importer} />)

    expect(await screen.findByRole('form', { name: 'Sign in' })).toBeInTheDocument()
    await new Promise((resolve) => setTimeout(resolve, 20))
    expect(screen.getByRole('form', { name: 'Sign in' })).toBeInTheDocument()
    expect(screen.queryByText('hello (built in)')).not.toBeInTheDocument()
  })

  it('names the product', () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([])))

    render(<App />)

    expect(screen.getByRole('heading', { name: 'NKeeper' })).toBeInTheDocument()
  })
})
