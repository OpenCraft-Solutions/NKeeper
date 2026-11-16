import { apiWebSocketUrl } from './api'

/**
 * Stream sessions (docs/architecture.md §7.10): one WebSocket per session. Core runs the control phase
 * (`start`, `status`, `hostKey`, `connected` or `error`); then the plugin owns the data, until `closed`.
 */

/** What the browser asks core to open. */
export interface StreamStart {
  readonly hostId: string
  readonly serviceId: string
  /** A saved credential of the host's, used with the service. */
  readonly credentialId?: string
  /** A typed credential, as core's credential prompt answered for it. */
  readonly promptReference?: string
  /** The plugin's own options (a terminal's size, say). */
  readonly options?: Record<string, unknown>
}

/** A server key no one trusted yet, as core shows it. */
export interface HostKeyPrompt {
  readonly hostId: string
  readonly serviceId: string
  readonly algorithm: string
  readonly fingerprint: string
}

/**
 * A server identity no one trusted yet (docs/architecture.md §7.7, §7.16): an SSH key, or a TLS certificate with
 * its subject and expiry. A plugin endpoint reports one when core refused to connect without it (SDK 0.1.8).
 */
export interface TrustPrompt extends HostKeyPrompt {
  readonly kind: 'ssh-host-key' | 'tls-certificate'
  readonly subject?: string
  readonly notAfterUtc?: string
}

export interface OpenStreamOptions {
  /** Asks the user about a key or certificate no one trusted yet; resolves true to trust it. */
  readonly confirmHostKey: (prompt: TrustPrompt) => Promise<boolean>
  /** Core's progress, e.g. `connecting`. */
  readonly onStatus?: (status: string) => void
  /** Aborting before `connected` closes the socket and rejects with code `aborted`. */
  readonly signal?: AbortSignal
  /** For tests: makes the socket. */
  readonly createSocket?: (url: string) => WebSocket
}

/**
 * Why a session didn't open: one of core's error codes (`host_not_found`, `host_key_rejected`,
 * `authentication_failed`, …), or `disconnected` when the socket closed first, or `aborted`.
 */
export class SessionError extends Error {
  readonly code: string

  constructor(code: string, message: string) {
    super(message)
    this.name = 'SessionError'
    this.code = code
  }
}

/** An open session's data phase. */
export interface StreamConnection {
  /** Sends a binary frame. Text is sent as its UTF-8 bytes. */
  send(data: Uint8Array<ArrayBuffer> | string): void
  /** Sends a JSON text frame, e.g. `{ type: 'resize', … }`. */
  sendJson(message: object): void
  /** Sends a text frame as it is: a Guacamole instruction, say (SDK 0.1.9). */
  sendText(text: string): void
  /** The plugin's binary frames. Frames that came before the first listener are delivered to it. */
  onBinary(listener: (data: Uint8Array) => void): () => void
  /** The plugin's text frames, as they came. */
  onText(listener: (text: string) => void): () => void
  /** Core's reason when the session ends (`closed`, `access_revoked`, `failed`), or `disconnected`. */
  readonly closed: Promise<string>
  /** Ends the session. */
  close(): void
}

interface ControlMessage {
  type?: unknown
  status?: unknown
  code?: unknown
  message?: unknown
  reason?: unknown
  hostId?: unknown
  serviceId?: unknown
  algorithm?: unknown
  fingerprint?: unknown
  kind?: unknown
  subject?: unknown
  notAfterUtc?: unknown
}

function parseControl(text: string): ControlMessage | null {
  try {
    const value: unknown = JSON.parse(text)
    return typeof value === 'object' && value !== null && !Array.isArray(value) ? (value as ControlMessage) : null
  } catch {
    return null
  }
}

/** Fans frames out to listeners, keeping those that came before the first listener. */
class Frames<T> {
  private readonly listeners = new Set<(frame: T) => void>()
  private pending: T[] | null = []

  push(frame: T): void {
    if (this.pending) {
      this.pending.push(frame)
    } else {
      this.listeners.forEach((listener) => listener(frame))
    }
  }

  listen(listener: (frame: T) => void): () => void {
    this.listeners.add(listener)
    const pending = this.pending
    this.pending = null
    pending?.forEach(listener)
    return () => {
      this.listeners.delete(listener)
    }
  }
}

/** Opens a stream session of `type`, running core's control phase, and resolves once core says `connected`. */
export function openStreamSession(type: string, start: StreamStart, options: OpenStreamOptions): Promise<StreamConnection> {
  const socket = (options.createSocket ?? ((url) => new WebSocket(url)))(apiWebSocketUrl(`/api/sessions/${encodeURIComponent(type)}`))
  socket.binaryType = 'arraybuffer'
  const binary = new Frames<Uint8Array>()
  const text = new Frames<string>()
  let reason = 'disconnected'
  let resolveClosed: (reason: string) => void = () => {}
  const closed = new Promise<string>((resolve) => {
    resolveClosed = resolve
  })

  const connection: StreamConnection = {
    send(data) {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(typeof data === 'string' ? new TextEncoder().encode(data) : data)
      }
    },
    sendJson(message) {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(JSON.stringify(message))
      }
    },
    sendText(text) {
      if (socket.readyState === WebSocket.OPEN) {
        socket.send(text)
      }
    },
    onBinary: (listener) => binary.listen(listener),
    onText: (listener) => text.listen(listener),
    closed,
    close() {
      socket.close(1000, 'Closed')
    },
  }

  return new Promise<StreamConnection>((resolve, reject) => {
    let connected = false
    let settled = false
    const fail = (code: string, message: string) => {
      if (!settled) {
        settled = true
        reject(new SessionError(code, message))
      }
      if (socket.readyState === WebSocket.CONNECTING || socket.readyState === WebSocket.OPEN) {
        socket.close(1000, 'Closed')
      }
    }

    const abort = () => fail('aborted', 'The session was cancelled.')
    if (options.signal?.aborted) {
      abort()
      return
    }
    options.signal?.addEventListener('abort', abort, { once: true })

    socket.addEventListener('open', () => {
      socket.send(JSON.stringify({ type: 'start', ...start }))
    })

    socket.addEventListener('message', (event: MessageEvent) => {
      if (connected) {
        if (typeof event.data === 'string') {
          const message = parseControl(event.data)
          if (message?.type === 'closed' && typeof message.reason === 'string') {
            reason = message.reason
          } else {
            text.push(event.data)
          }
        } else {
          binary.push(new Uint8Array(event.data as ArrayBuffer))
        }
        return
      }

      const message = typeof event.data === 'string' ? parseControl(event.data) : null
      switch (message?.type) {
        case 'status':
          options.onStatus?.(String(message.status))
          break
        case 'hostKey':
          void options
            .confirmHostKey({
              kind: message.kind === 'tls-certificate' ? 'tls-certificate' : 'ssh-host-key',
              hostId: String(message.hostId),
              serviceId: String(message.serviceId),
              algorithm: String(message.algorithm),
              fingerprint: String(message.fingerprint),
              ...(typeof message.subject === 'string' ? { subject: message.subject } : {}),
              ...(typeof message.notAfterUtc === 'string' ? { notAfterUtc: message.notAfterUtc } : {}),
            })
            .catch(() => false)
            .then((trust) => {
              if (socket.readyState === WebSocket.OPEN) {
                socket.send(JSON.stringify({ type: 'hostKeyDecision', trust }))
              }
            })
          break
        case 'connected':
          connected = true
          settled = true
          options.signal?.removeEventListener('abort', abort)
          resolve(connection)
          break
        case 'error':
          fail(typeof message.code === 'string' ? message.code : 'failed', typeof message.message === 'string' ? message.message : 'The session could not be opened.')
          break
        default:
          // Nothing else is meaningful before the session opens.
          break
      }
    })

    socket.addEventListener('close', () => {
      options.signal?.removeEventListener('abort', abort)
      fail('disconnected', 'The connection to the server closed before the session opened.')
      resolveClosed(reason)
    })
  })
}
