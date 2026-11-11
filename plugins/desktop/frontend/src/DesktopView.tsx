import { useEffect, useRef, useState } from 'react'
import { SessionError, type SessionViewProps, type StreamConnection } from '@nkeeper/sdk'
import { describeDesktopStatus } from './desktopStatus'
import Guacamole from './vendor/guacamole-common-js/guacamole.js'

/** The view's own styles, adopted when the plugin activates. */
export const desktopStyles = `
.nk-desktop { position: relative; display: flex; flex-direction: column; height: 100%; min-height: 0; background: #111; }
.nk-desktop-toolbar { display: flex; gap: 8px; padding: 4px 8px; background: #1b1b1b; }
.nk-desktop-toolbar button { font: 12px system-ui, sans-serif; }
.nk-desktop-viewport { flex: 1; min-height: 0; overflow: hidden; outline: none; display: flex; align-items: center; justify-content: center; }
.nk-desktop-overlay { position: absolute; inset: auto 0 0 0; display: flex; gap: 12px; align-items: center; padding: 8px 12px;
  background: rgba(20, 20, 20, 0.92); color: #eee; font: 14px system-ui, sans-serif; }
`

type State = { kind: 'connecting' } | { kind: 'open' } | { kind: 'ended'; message: string }

const END_MESSAGES: Record<string, string> = {
  closed: 'The desktop session ended.',
  access_revoked: 'Your access to this host changed, so the session ended.',
  failed: 'The desktop session failed.',
  disconnected: 'The connection to the server was lost.',
}

const RESIZE_DEBOUNCE_MS = 300

/** The remote desktop's size in physical pixels, for the viewport it's shown in. */
function desktopSize(viewport: HTMLElement) {
  const ratio = window.devicePixelRatio || 1
  return { width: Math.max(200, Math.round(viewport.clientWidth * ratio)), height: Math.max(200, Math.round(viewport.clientHeight * ratio)), dpi: Math.round(96 * ratio) }
}

/** A Guacamole instruction, as the client's tunnel hands it over. */
function toInstruction(elements: unknown[]): string {
  return (
    elements
      .map((element) => {
        const text = String(element)
        return `${[...text].length}.${text}`
      })
      .join(',') + ';'
  )
}

const TunnelBase = Guacamole.Tunnel as unknown as new () => InstanceType<typeof Guacamole.Tunnel> & { setState: (state: number) => void }

/**
 * The rdp session type's tab (docs/architecture.md §7.21): Apache Guacamole's own client, drawing the desktop core
 * connects through guacd. Ported from 1.x's DesktopSession; core's stream session replaces 1.x's own socket.
 */
export function DesktopView({ connect, visible }: SessionViewProps) {
  const viewportRef = useRef<HTMLDivElement>(null)
  const clientRef = useRef<InstanceType<typeof Guacamole.Client> | null>(null)
  const lastClipboardRef = useRef('')
  const [state, setState] = useState<State>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const viewport = viewportRef.current
    if (!viewport) {
      return
    }
    const opening = new AbortController()
    let disposed = false
    let connection: StreamConnection | null = null
    let client: InstanceType<typeof Guacamole.Client> | null = null
    let keyboard: InstanceType<typeof Guacamole.Keyboard> | null = null
    const cleanups: (() => void)[] = []
    const timezone = Intl.DateTimeFormat().resolvedOptions().timeZone

    connect({ ...desktopSize(viewport), timezone }, opening.signal).then(
      (opened) => {
        if (disposed) {
          opened.close()
          return
        }
        connection = opened
        setState({ kind: 'open' })

        // Core relays Guacamole text: the client's tunnel is the session's connection.
        const tunnel = new TunnelBase()
        tunnel.connect = () => tunnel.setState(Guacamole.Tunnel.State.OPEN)
        tunnel.disconnect = () => tunnel.setState(Guacamole.Tunnel.State.CLOSED)
        tunnel.sendMessage = (...elements: unknown[]) => opened.sendText(toInstruction(elements))
        const parser = new Guacamole.Parser()
        parser.oninstruction = (opcode: string, args: unknown[]) => tunnel.oninstruction?.(opcode, args)
        cleanups.push(opened.onText((text) => parser.receive(text)))

        client = new Guacamole.Client(tunnel)
        clientRef.current = client
        const display = client.getDisplay()
        const element = display.getElement()
        viewport.replaceChildren(element)

        // Fit the remote desktop, sized in physical pixels, into the viewport.
        display.onresize = () => {
          const scale = Math.min(viewport.clientWidth / display.getWidth(), viewport.clientHeight / display.getHeight())
          if (Number.isFinite(scale) && scale > 0) {
            display.scale(scale)
          }
        }

        client.onerror = (status) => {
          // Status 0 is a clean end (the user signed out, say).
          if (status.code !== 0 && !disposed) {
            setState({ kind: 'ended', message: describeDesktopStatus(status, 'rdp') })
          }
        }

        // Remote → local clipboard, text only: core's allow-list takes nothing else from the browser either.
        client.onclipboard = (stream, mimetype) => {
          if (mimetype !== 'text/plain') {
            return
          }
          const reader = new Guacamole.StringReader(stream)
          let text = ''
          reader.ontext = (chunk: string) => {
            text += chunk
          }
          reader.onend = () => {
            lastClipboardRef.current = text
            void navigator.clipboard?.writeText(text).catch(() => {})
          }
        }

        const mouse = new Guacamole.Mouse(element)
        mouse.onEach(['mousedown', 'mouseup', 'mousemove'], (event) => {
          client?.sendMouseState((event as unknown as { state: InstanceType<typeof Guacamole.Mouse.State> }).state, true)
        })
        keyboard = new Guacamole.Keyboard(viewport)
        keyboard.onkeydown = (keysym: number) => {
          client?.sendKeyEvent(1, keysym)
          return false
        }
        keyboard.onkeyup = (keysym: number) => client?.sendKeyEvent(0, keysym)

        client.connect()
        viewport.focus()
        void opened.closed.then((reason) => {
          if (!disposed) {
            // guacd's own error, when it sent one, says more than the close that follows it.
            setState((current) => (current.kind === 'ended' ? current : { kind: 'ended', message: END_MESSAGES[reason] ?? END_MESSAGES.closed! }))
          }
        })
      },
      (error: unknown) => {
        if (!disposed) {
          setState({ kind: 'ended', message: error instanceof SessionError ? error.message : 'The desktop couldn’t be opened.' })
        }
      },
    )

    // Keep the remote resolution matched to the viewport.
    let resizeTimer: number | undefined
    const observer = new ResizeObserver(() => {
      window.clearTimeout(resizeTimer)
      resizeTimer = window.setTimeout(() => {
        if (client && viewport.clientWidth > 0 && viewport.clientHeight > 0) {
          const { width, height } = desktopSize(viewport)
          client.sendSize(width, height)
          client.getDisplay().onresize?.(client.getDisplay().getWidth(), client.getDisplay().getHeight())
        }
      }, RESIZE_DEBOUNCE_MS)
    })
    observer.observe(viewport)

    return () => {
      disposed = true
      opening.abort()
      observer.disconnect()
      window.clearTimeout(resizeTimer)
      keyboard?.reset()
      cleanups.forEach((cleanup) => cleanup())
      // Ends the RDP session cleanly (sends "disconnect") rather than just dropping it.
      client?.disconnect()
      clientRef.current = null
      viewport.replaceChildren()
      connection?.close()
    }
  }, [connect, attempt])

  // The showing tab gets the keyboard.
  useEffect(() => {
    if (visible && clientRef.current) {
      viewportRef.current?.focus()
    }
  }, [visible])

  /** Local → remote clipboard, when the desktop gains focus (the browser may ask for permission). */
  function syncClipboardToRemote() {
    const client = clientRef.current
    if (!client || !navigator.clipboard?.readText) {
      return
    }
    navigator.clipboard.readText().then(
      (text) => {
        if (text && text !== lastClipboardRef.current) {
          lastClipboardRef.current = text
          const writer = new Guacamole.StringWriter(client.createClipboardStream('text/plain'))
          writer.sendText(text)
          writer.sendEnd()
        }
      },
      () => {
        // Permission denied or unavailable: skip.
      },
    )
  }

  function sendCtrlAltDel() {
    const client = clientRef.current
    // Control_L, Alt_L, Delete.
    for (const keysym of [0xffe3, 0xffe9, 0xffff]) {
      client?.sendKeyEvent(1, keysym)
    }
    for (const keysym of [0xffff, 0xffe9, 0xffe3]) {
      client?.sendKeyEvent(0, keysym)
    }
  }

  return (
    <div className="nk-desktop">
      <div className="nk-desktop-toolbar">
        <button type="button" onClick={sendCtrlAltDel} disabled={state.kind !== 'open'}>
          Ctrl+Alt+Del
        </button>
      </div>
      <div ref={viewportRef} className="nk-desktop-viewport" tabIndex={0} onFocus={syncClipboardToRemote} aria-label="Remote desktop" />
      {state.kind === 'connecting' && (
        <div className="nk-desktop-overlay" role="status">
          Connecting…
        </div>
      )}
      {state.kind === 'ended' && (
        <div className="nk-desktop-overlay" role="status">
          <span>{state.message}</span>
          <button
            type="button"
            onClick={() => {
              setState({ kind: 'connecting' })
              setAttempt((n) => n + 1)
            }}
          >
            Reconnect
          </button>
        </div>
      )}
    </div>
  )
}
