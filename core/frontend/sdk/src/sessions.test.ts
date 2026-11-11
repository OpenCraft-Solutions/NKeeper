import { describe, expect, it, vi } from 'vitest'
import { SessionError, openStreamSession, type OpenStreamOptions } from './sessions'

/** A WebSocket the test drives: it records what's sent, and plays the server's side. */
class FakeSocket extends EventTarget {
  static readonly CONNECTING = 0
  static readonly OPEN = 1
  static readonly CLOSING = 2
  static readonly CLOSED = 3
  readyState = FakeSocket.CONNECTING
  binaryType = 'blob'
  readonly sent: unknown[] = []
  readonly url: string

  constructor(url: string) {
    super()
    this.url = url
  }

  open() {
    this.readyState = FakeSocket.OPEN
    this.dispatchEvent(new Event('open'))
  }

  receive(data: string | ArrayBuffer) {
    this.dispatchEvent(new MessageEvent('message', { data }))
  }

  receiveJson(message: object) {
    this.receive(JSON.stringify(message))
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

function open(overrides: Partial<OpenStreamOptions> = {}) {
  vi.stubGlobal('WebSocket', FakeSocket)
  let socket: FakeSocket | undefined
  const session = openStreamSession(
    'ssh',
    { hostId: 'h', serviceId: 's', credentialId: 'c', options: { columns: 80 } },
    {
      confirmHostKey: () => Promise.resolve(false),
      ...overrides,
      createSocket: (url) => {
        socket = new FakeSocket(url)
        return socket as unknown as WebSocket
      },
    },
  )
  return { session, socket: socket! }
}

const flush = () => new Promise((resolve) => setTimeout(resolve, 0))

describe('openStreamSession', () => {
  it('sends start on its type’s URL, reports status and resolves on connected', async () => {
    const statuses: string[] = []
    const { session, socket } = open({ onStatus: (s) => statuses.push(s) })
    expect(socket.url).toBe(`ws://${window.location.host}/api/sessions/ssh`)
    socket.open()
    expect(socket.sent).toEqual([{ type: 'start', hostId: 'h', serviceId: 's', credentialId: 'c', options: { columns: 80 } }])
    socket.receiveJson({ type: 'status', status: 'connecting' })
    socket.receiveJson({ type: 'connected' })
    await session
    expect(statuses).toEqual(['connecting'])
  })

  it('asks about a server key and sends the decision', async () => {
    const confirmHostKey = vi.fn(() => Promise.resolve(true))
    const { session, socket } = open({ confirmHostKey })
    socket.open()
    socket.receiveJson({ type: 'hostKey', hostId: 'h', serviceId: 's', algorithm: 'ssh-ed25519', fingerprint: 'abc' })
    await flush()
    expect(confirmHostKey).toHaveBeenCalledWith({ hostId: 'h', serviceId: 's', algorithm: 'ssh-ed25519', fingerprint: 'abc' })
    expect(socket.sent.at(-1)).toEqual({ type: 'hostKeyDecision', trust: true })
    socket.receiveJson({ type: 'connected' })
    await session
  })

  it('rejects with core’s error code', async () => {
    const { session, socket } = open()
    socket.open()
    socket.receiveJson({ type: 'error', code: 'authentication_failed', message: 'No.' })
    await expect(session).rejects.toEqual(new SessionError('authentication_failed', 'No.'))
    expect(socket.readyState).toBe(FakeSocket.CLOSED)
  })

  it('rejects when the socket closes before connected, or when aborted', async () => {
    const { session, socket } = open()
    socket.close()
    await expect(session).rejects.toMatchObject({ code: 'disconnected' })

    const controller = new AbortController()
    const aborted = open({ signal: controller.signal })
    controller.abort()
    await expect(aborted.session).rejects.toMatchObject({ code: 'aborted' })
  })

  it('hands the plugin its frames, early ones included, and reports core’s closing reason', async () => {
    const { session, socket } = open()
    socket.open()
    socket.receiveJson({ type: 'connected' })
    const connection = await session

    socket.receive(new TextEncoder().encode('early').buffer)
    const received: string[] = []
    connection.onBinary((data) => received.push(new TextDecoder().decode(data)))
    socket.receive(new TextEncoder().encode('late').buffer)
    const texts: string[] = []
    connection.onText((t) => texts.push(t))
    socket.receive('{"type":"title","title":"x"}')
    expect(received).toEqual(['early', 'late'])
    expect(texts).toEqual(['{"type":"title","title":"x"}'])

    connection.send('ls\n')
    connection.sendJson({ type: 'resize', columns: 100, rows: 30 })
    expect(new TextDecoder().decode(socket.sent.at(-2) as Uint8Array)).toBe('ls\n')
    expect(socket.sent.at(-1)).toEqual({ type: 'resize', columns: 100, rows: 30 })

    socket.receiveJson({ type: 'closed', reason: 'access_revoked' })
    socket.close()
    await expect(connection.closed).resolves.toBe('access_revoked')
  })
})
