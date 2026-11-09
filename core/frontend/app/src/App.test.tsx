import { render, screen } from '@testing-library/react'
import { definePlugin } from '@nkeeper/sdk'
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

  it('names the product', () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([])))

    render(<App />)

    expect(screen.getByRole('heading', { name: 'NKeeper' })).toBeInTheDocument()
  })
})
