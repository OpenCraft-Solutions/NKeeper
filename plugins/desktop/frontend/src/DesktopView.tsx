import { useEffect, useRef, useState } from 'react'
import { SessionError, type SessionViewProps, type StreamConnection } from '@nkeeper/sdk'
import { describeDesktopStatus } from './desktopStatus'
import Guacamole from './vendor/guacamole-common-js/guacamole.js'

/**
 * The view's own styles, adopted when the plugin activates: 1.x's session look (a status bar over the desktop,
 * and cards for opening and ending), on the shell's design tokens, each with a fallback (docs/architecture.md §7.14).
 */
export const desktopStyles = `
.nk-desktop { display: flex; flex-direction: column; height: 100%; min-height: 0; background: var(--color-console-bg, #0b1220);
  color: var(--color-text, #e5ebf5); font-family: var(--font-sans, system-ui, sans-serif); }
.nk-desktop-status { display: flex; align-items: center; gap: 0.5rem; min-height: 2.25rem; padding: 0.25rem 1rem;
  background: var(--color-surface, #111a2e); border-bottom: 1px solid var(--color-border, #233150); font-size: 0.8125rem; }
.nk-desktop-dot { width: 0.5rem; height: 0.5rem; flex-shrink: 0; border-radius: 999px; background: var(--color-text-subtle, #7d8ba6); }
.nk-desktop-dot[data-state='connecting'] { background: var(--color-warning, #f5b84b); }
.nk-desktop-dot[data-state='open'] { background: #4ade80; }
.nk-desktop-dot[data-state='ended'] { background: #f87171; }
.nk-desktop-status-text { font-weight: 550; }
.nk-desktop-target { overflow: hidden; color: var(--color-text-muted, #9aa8c1); font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
  text-overflow: ellipsis; white-space: nowrap; }
.nk-desktop-actions { display: flex; align-items: center; gap: 0.5rem; margin-left: auto; }
.nk-desktop-area { position: relative; flex: 1; min-height: 0; }
.nk-desktop-area:fullscreen { background: #000; }
.nk-desktop-viewport { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; overflow: hidden;
  background: #000; cursor: default; outline: none; }
.nk-desktop-viewport:focus-visible { box-shadow: inset 0 0 0 2px var(--color-focus, #60a5fa); }
.nk-desktop-overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 1rem;
  background: rgb(11 18 32 / 0.75); }
.nk-desktop-card { display: flex; flex-direction: column; gap: 0.75rem; max-width: 32rem; padding: 1.25rem 1.5rem;
  background: var(--color-surface, #111a2e); border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-lg, 12px);
  box-shadow: var(--shadow-card, 0 1px 2px rgb(0 0 0 / 0.3), 0 8px 24px rgb(0 0 0 / 0.25)); font-size: 0.9375rem; }
.nk-desktop-card[data-error] { border-color: var(--color-danger-border, #b91c1c); }
.nk-desktop-card-row { flex-direction: row; align-items: center; }
.nk-desktop-card h3 { margin: 0; font-size: 1.0625rem; font-weight: 620; }
.nk-desktop-card p { margin: 0; }
.nk-desktop-card-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 0.5rem; }
.nk-desktop-spinner { width: 1rem; height: 1rem; border: 2px solid var(--color-border-strong, #33456b); border-top-color: var(--color-accent-text, #8fb4ff);
  border-radius: 999px; animation: nk-desktop-spin 0.8s linear infinite; }
@keyframes nk-desktop-spin { to { transform: rotate(360deg); } }
.nk-desktop-button { display: inline-flex; align-items: center; justify-content: center; min-height: 2rem; padding: 0 0.75rem;
  border: 1px solid transparent; border-radius: var(--radius-md, 8px); background: transparent; color: var(--color-text, #e5ebf5);
  font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; }
.nk-desktop-button:hover { background: var(--color-surface-raised, #16213a); }
.nk-desktop-button[data-secondary] { border-color: var(--color-border-strong, #33456b); }
.nk-desktop-button[data-primary] { background: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-desktop-button[data-primary]:hover { background: var(--color-accent-hover, #1d4ed8); }
`

/** Ended: why, and whether it failed before it opened. */
type State = { kind: 'connecting' } | { kind: 'open' } | { kind: 'ended'; message: string; failed: boolean }

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
export function DesktopView({ connect, visible, host }: SessionViewProps) {
  const areaRef = useRef<HTMLDivElement>(null)
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
            setState({ kind: 'ended', message: describeDesktopStatus(status, 'rdp'), failed: true })
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
            setState((current) => (current.kind === 'ended' ? current : { kind: 'ended', message: END_MESSAGES[reason] ?? END_MESSAGES.closed!, failed: false }))
          }
        })
      },
      (error: unknown) => {
        if (!disposed) {
          setState({ kind: 'ended', message: error instanceof SessionError ? error.message : 'The desktop couldn’t be opened.', failed: true })
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

  function reconnect() {
    setState({ kind: 'connecting' })
    setAttempt((n) => n + 1)
  }

  async function fullscreen() {
    // Chromium can then capture keys like Win, Alt+Tab and Esc for the remote desktop.
    if (document.fullscreenElement) {
      await document.exitFullscreen().catch(() => {})
    } else {
      await areaRef.current?.requestFullscreen().catch(() => {})
    }
    viewportRef.current?.focus()
  }

  return (
    <div className="nk-desktop">
      <div className="nk-desktop-status" role="status">
        <span className="nk-desktop-dot" data-state={state.kind} />
        <span className="nk-desktop-status-text">{state.kind === 'open' ? 'Connected' : state.kind === 'ended' ? 'Not connected' : 'Connecting…'}</span>
        <span className="nk-desktop-target">{host.name}</span>
        <div className="nk-desktop-actions">
          {state.kind === 'open' && (
            <>
              <button type="button" className="nk-desktop-button" onClick={sendCtrlAltDel}>
                Ctrl+Alt+Del
              </button>
              <button type="button" className="nk-desktop-button" onClick={() => void fullscreen()}>
                Fullscreen
              </button>
            </>
          )}
          {state.kind === 'ended' && (
            <button type="button" className="nk-desktop-button" data-secondary onClick={reconnect}>
              Reconnect
            </button>
          )}
        </div>
      </div>
      <div ref={areaRef} className="nk-desktop-area">
        <div
          ref={viewportRef}
          className="nk-desktop-viewport"
          tabIndex={0}
          aria-label="Remote desktop. Keyboard input goes to the remote computer while this has focus."
          onMouseDown={() => viewportRef.current?.focus()}
          onFocus={syncClipboardToRemote}
        />
        {state.kind === 'connecting' && (
          <div className="nk-desktop-overlay">
            <div className="nk-desktop-card nk-desktop-card-row">
              <span className="nk-desktop-spinner" aria-hidden="true" /> Opening session…
            </div>
          </div>
        )}
        {state.kind === 'ended' && (
          <div className="nk-desktop-overlay">
            <div className="nk-desktop-card" data-error={state.failed || undefined} role="alert">
              <h3>{state.failed ? 'Couldn’t connect' : 'Session ended'}</h3>
              <p>{state.message}</p>
              <div className="nk-desktop-card-actions">
                <button type="button" className="nk-desktop-button" data-primary onClick={reconnect}>
                  {state.failed ? 'Try again' : 'Reconnect'}
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
