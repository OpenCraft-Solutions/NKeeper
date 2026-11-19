import { describe, expect, it, vi } from 'vitest'
import { ApiError, api, definePlugin, isPluginDefinition, onUnauthorized, readJsonLines } from './index'

describe('definePlugin', () => {
  it('returns a frozen definition', () => {
    const plugin = definePlugin({ id: 'com.example.tool', activate: () => {} })

    expect(plugin.id).toBe('com.example.tool')
    expect(Object.isFrozen(plugin)).toBe(true)
  })

  it.each([{ id: 'Bad_Id' }, { id: '' }, { id: 'ok', activate: 'not a function' }, null])('rejects %j', (value) => {
    expect(() => definePlugin(value as never)).toThrow(TypeError)
    expect(isPluginDefinition(value)).toBe(false)
  })
})

describe('api', () => {
  it('returns parsed JSON', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json([{ id: 'hello' }])))

    await expect(api.get('/api/plugins')).resolves.toEqual([{ id: 'hello' }])
    expect(fetch).toHaveBeenCalledWith('/api/plugins', expect.objectContaining({ method: 'GET', credentials: 'same-origin' }))
  })

  it('turns problem details into an ApiError', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Response.json({ title: 'Nope', detail: 'Not allowed.', code: 'forbidden' }, { status: 403 })))

    const error = await api.get('/api/x').catch((e: unknown) => e)

    expect(error).toBeInstanceOf(ApiError)
    expect(error).toMatchObject({ status: 403, message: 'Not allowed.', code: 'forbidden' })
  })

  it('reports a network failure as status 0', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => Promise.reject(new TypeError('offline'))))

    const error = (await api.get('/api/x').catch((e: unknown) => e)) as ApiError

    expect(error.isNetworkError).toBe(true)
  })

  it('tells listeners about a 401', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })))
    const listener = vi.fn()
    const unsubscribe = onUnauthorized(listener)

    await api.get('/api/x').catch(() => {})
    unsubscribe()

    expect(listener).toHaveBeenCalledOnce()
  })
})

describe('readJsonLines', () => {
  function streamOf(...chunks: string[]): Response {
    const body = new ReadableStream<Uint8Array>({
      start(controller) {
        chunks.forEach((chunk) => controller.enqueue(new TextEncoder().encode(chunk)))
        controller.close()
      },
    })
    return new Response(body)
  }

  it('hands over records split across chunks, skipping blank lines', async () => {
    const lines: unknown[] = []

    await readJsonLines(streamOf('{"id":1}\n{"i', 'd":2}\n\n'), (line) => lines.push(line))

    expect(lines).toEqual([{ id: 1 }, { id: 2 }])
  })

  it('hands over a last record that has no trailing newline', async () => {
    const lines: unknown[] = []

    await readJsonLines(streamOf('{"id":1}\n{"id":2}'), (line) => lines.push(line))

    expect(lines).toEqual([{ id: 1 }, { id: 2 }])
  })
})

describe('session views', () => {
  it('accepts function and React component objects, and nothing else', () => {
    const memo = { $$typeof: Symbol.for('react.memo'), type: () => null }
    expect(isPluginDefinition({ id: 'term', sessionViews: { a: () => null, b: memo } })).toBe(true)
    expect(isPluginDefinition({ id: 'term', sessionViews: { a: {} } })).toBe(false)
    expect(isPluginDefinition({ id: 'term', sessionViews: { a: [] } })).toBe(false)
    expect(isPluginDefinition({ id: 'term', sessionViews: [] })).toBe(false)
    expect(isPluginDefinition({ id: 'term', sessionViews: { a: 'TerminalView' } })).toBe(false)
    expect(isPluginDefinition({ id: 'core', exports: { QueryView: () => null, limit: 3 } })).toBe(true)
    expect(isPluginDefinition({ id: 'core', exports: [] })).toBe(false)
    expect(isPluginDefinition({ id: 'core', exports: null })).toBe(false)
  })
})

describe('host and settings panels (SDK 1.13)', () => {
  it('takes panels by title and one settings component', () => {
    const panel = () => null
    expect(isPluginDefinition({ id: 'zabbix', hostPanels: { Zabbix: panel }, settingsPanel: panel })).toBe(true)
    expect(isPluginDefinition({ id: 'zabbix', hostPanels: { ' ': panel } })).toBe(false)
    expect(isPluginDefinition({ id: 'zabbix', hostPanels: { Zabbix: 'panel' } })).toBe(false)
    expect(isPluginDefinition({ id: 'zabbix', hostPanels: [panel] })).toBe(false)
    expect(isPluginDefinition({ id: 'zabbix', settingsPanel: {} })).toBe(false)
    expect(Object.isFrozen(definePlugin({ id: 'zabbix', hostPanels: { Zabbix: panel } }).hostPanels)).toBe(true)
  })
})

describe('reports and inventory tools (SDK 1.14)', () => {
  it('takes them by ID, each with a label and a view', () => {
    const view = () => null
    expect(isPluginDefinition({ id: 'ip', reports: { 'ip-addresses': { label: 'IP addresses', description: 'By subnet', view } } })).toBe(true)
    expect(isPluginDefinition({ id: 'ip', reports: { 'ip-addresses': { label: 'IP addresses', description: 'By subnet', view, administrators: true } } })).toBe(true)
    expect(isPluginDefinition({ id: 'ip', reports: { 'IP addresses': { label: 'IP addresses', view } } })).toBe(false)
    expect(isPluginDefinition({ id: 'ip', reports: { ip: { label: '', view } } })).toBe(false)
    expect(isPluginDefinition({ id: 'ip', reports: { ip: { label: 'IP', view: 'IpReport' } } })).toBe(false)
    expect(isPluginDefinition({ id: 'scan', inventoryTools: { 'network-scan': { label: 'Scan network', title: 'Loud', view } } })).toBe(true)
    expect(isPluginDefinition({ id: 'scan', inventoryTools: { 'network-scan': { label: 'Scan network', title: 3, view } } })).toBe(false)
  })
})
