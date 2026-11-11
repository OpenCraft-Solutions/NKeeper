import { vi } from 'vitest'
import type { PluginStatus } from '../plugins'
import type { Host, HostCredential, HostService } from '../types'

/** Test data and a fake API for the shell's tests. */

export function status(overrides: Partial<PluginStatus> = {}): PluginStatus {
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

/** A plugin contributing a service type, a session type on it, and a Connect action, all named `term`. */
export function terminalPlugin(overrides: Partial<PluginStatus> = {}): PluginStatus {
  return status({
    id: 'term',
    frontendEntry: '/plugins/term/1.0.0/aaaaaaaaaaaa/index.js',
    serviceTypes: [
      {
        id: 'term',
        label: 'Term',
        defaultPort: 22,
        settingsVersion: 1,
        health: 'tcp',
        settings: [
          { key: 'shell', label: 'Shell', kind: 'text', required: false, maxLength: 50, minimum: null, maximum: null, choices: null },
          { key: 'mode', label: 'Mode', kind: 'choice', required: true, maxLength: null, minimum: null, maximum: null, choices: ['fast', 'safe'] },
          { key: 'retries', label: 'Retries', kind: 'number', required: false, maxLength: null, minimum: 1, maximum: 5, choices: null },
          { key: 'compress', label: 'Compress', kind: 'switch', required: false, maxLength: null, minimum: null, maximum: null, choices: null },
        ],
      },
    ],
    sessionTypes: [{ id: 'term', label: 'Terminal', serviceTypes: ['term'], supportsJumpHost: true }],
    hostActions: [
      { id: 'term.open', label: 'Terminal', sessionType: 'term', endpoint: null, serviceTypes: [], credential: 'required' },
      // A run action (SDK 0.1.7), listed first, so double-clicking must skip it.
      { id: 'term.reboot', label: 'Reboot', sessionType: null, endpoint: 'reboot', serviceTypes: ['term'], credential: 'none' },
    ],
    ...overrides,
  })
}

export function service(overrides: Partial<HostService> = {}): HostService {
  return { id: 's1', type: 'term', label: null, port: null, address: null, settings: {}, available: true, ...overrides }
}

export function credential(overrides: Partial<HostCredential> = {}): HostCredential {
  return { id: 'c1', label: null, username: 'root', domain: null, hasPassword: true, hasPrivateKey: false, notes: null, serviceIndexes: null, ...overrides }
}

export function host(overrides: Partial<Host> = {}): Host {
  return {
    id: 'h1',
    name: 'web01',
    hostname: 'web01.example.com',
    ipAddress: '192.0.2.10',
    description: null,
    tags: [],
    services: [],
    credentials: [],
    jump: null,
    trustedKeys: [],
    createdAtUtc: '2026-01-01T00:00:00Z',
    updatedAtUtc: '2026-01-01T00:00:00Z',
    version: 1,
    canManage: true,
    ...overrides,
  }
}

export const ADMIN = { id: 'u1', userName: 'admin', email: null, roles: ['Administrator'], mustChangePassword: false }

type Handler = (init: RequestInit | undefined, url: string) => Response | Promise<Response>

export function problem(status: number, body: object = {}): Response {
  return Response.json({ status, ...body }, { status, headers: { 'Content-Type': 'application/problem+json' } })
}

/**
 * Stubs fetch with routes keyed `METHOD /path`. The antiforgery token is always there; anything unrouted
 * is a 404. Returns the mock, to inspect its calls.
 */
export function fakeApi(routes: Record<string, Handler | object>) {
  const fetch = vi.fn(async (url: string, init?: RequestInit) => {
    const key = `${init?.method ?? 'GET'} ${url}`
    if (key === 'GET /api/auth/antiforgery') {
      return Response.json({ token: 'token-1', headerName: 'X-XSRF-TOKEN' })
    }
    const route = routes[key]
    if (route === undefined) {
      return problem(404)
    }
    return typeof route === 'function' ? (route as Handler)(init, url) : Response.json(route)
  })
  vi.stubGlobal('fetch', fetch)
  return fetch
}

/** The JSON body a call sent. */
export function bodyOf(fetch: ReturnType<typeof fakeApi>, key: string): unknown {
  const [method, path] = key.split(' ')
  const call = fetch.mock.calls.find(([url, init]) => url === path && (init?.method ?? 'GET') === method)
  return call ? JSON.parse(call[1]!.body as string) : undefined
}

/** A WebSocket the test drives: it records what's sent, and plays the server's side. The last one made is `FakeSocket.last`. */
export class FakeSocket extends EventTarget {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSING = 2
  static readonly CLOSED = 3
  static last: FakeSocket | null = null
  readyState = FakeSocket.CONNECTING
  binaryType = 'blob'
  readonly sent: unknown[] = []
  readonly url: string

  constructor(url: string) {
    super()
    this.url = url
    FakeSocket.last = this
  }

  open() {
    this.readyState = FakeSocket.OPEN
    this.dispatchEvent(new Event('open'))
  }

  receiveJson(message: object) {
    this.dispatchEvent(new MessageEvent('message', { data: JSON.stringify(message) }))
  }

  send(data: unknown) {
    this.sent.push(typeof data === 'string' ? JSON.parse(data) : data)
  }

  close() {
    if (this.readyState !== FakeSocket.CLOSED) {
      this.readyState = FakeSocket.CLOSED
      this.dispatchEvent(new Event('close'))
    }
  }
}
