import { useEffect, useRef, useState } from 'react'
import { SessionError, mirrorInjectedStyles, type SessionViewProps, type StreamConnection } from '@nkeeper/sdk'
import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'

/** The view's own styles, adopted when the plugin activates. */
export const terminalStyles = `
.nk-ssh { position: relative; display: flex; flex-direction: column; height: 100%; min-height: 0; background: #000; }
.nk-ssh-terminal { flex: 1; min-height: 0; padding: 4px 0 0 6px; }
.nk-ssh-overlay { position: absolute; inset: auto 0 0 0; display: flex; gap: 12px; align-items: center; padding: 8px 12px;
  background: rgba(20, 20, 20, 0.92); color: #eee; font: 14px system-ui, sans-serif; }
.nk-ssh-overlay button { font: inherit; }
`

type State = { kind: 'connecting' } | { kind: 'open' } | { kind: 'ended'; message: string }

const END_MESSAGES: Record<string, string> = {
  closed: 'The session ended.',
  access_revoked: 'Your access to this host changed, so the session ended.',
  failed: 'The session failed.',
  disconnected: 'The connection to the server was lost.',
}

/** Fits the terminal to its element. A hidden element has no size, and is left as it is. */
function fitTo(fit: FitAddon | null) {
  try {
    fit?.fit()
  } catch {
    // Not attached yet.
  }
}

/** The ssh session type's tab: xterm.js, fitted to its tab, resizing the server's terminal with it. */
export function TerminalView({ connect, visible }: SessionViewProps) {
  const element = useRef<HTMLDivElement>(null)
  const fitRef = useRef<FitAddon | null>(null)
  const [state, setState] = useState<State>({ kind: 'connecting' })
  const [attempt, setAttempt] = useState(0)

  useEffect(() => {
    const container = element.current
    if (!container) {
      return
    }
    const stopMirroring = mirrorInjectedStyles(container)
    const terminal = new Terminal({
      cursorBlink: true,
      fontFamily: 'ui-monospace, SFMono-Regular, Menlo, Consolas, "Liberation Mono", monospace',
      fontSize: 14,
      scrollback: 5000,
    })
    const fit = new FitAddon()
    terminal.loadAddon(fit)
    terminal.open(container)
    fitRef.current = fit
    fitTo(fit)

    let disposed = false
    let connection: StreamConnection | null = null
    // Going before the session opens cancels its opening, and any dialog it's waiting on.
    const opening = new AbortController()
    const cleanups: (() => void)[] = []
    setState({ kind: 'connecting' })

    connect({ columns: terminal.cols, rows: terminal.rows }, opening.signal).then(
      (opened) => {
        if (disposed) {
          opened.close()
          return
        }
        connection = opened
        setState({ kind: 'open' })
        cleanups.push(opened.onBinary((data) => terminal.write(data)))
        const input = terminal.onData((data) => opened.send(data))
        const resize = terminal.onResize(({ cols, rows }) => opened.sendJson({ type: 'resize', columns: cols, rows }))
        cleanups.push(() => input.dispose(), () => resize.dispose())
        // The size may have changed while the session opened.
        opened.sendJson({ type: 'resize', columns: terminal.cols, rows: terminal.rows })
        terminal.focus()
        void opened.closed.then((reason) => {
          if (!disposed) {
            setState({ kind: 'ended', message: END_MESSAGES[reason] ?? END_MESSAGES.closed! })
          }
        })
      },
      (error: unknown) => {
        if (!disposed) {
          setState({ kind: 'ended', message: error instanceof SessionError ? error.message : 'The terminal could not be opened.' })
        }
      },
    )

    const resized = new ResizeObserver(() => fitTo(fit))
    resized.observe(container)
    return () => {
      disposed = true
      opening.abort()
      resized.disconnect()
      cleanups.forEach((cleanup) => cleanup())
      connection?.close()
      fitRef.current = null
      terminal.dispose()
      stopMirroring()
    }
  }, [connect, attempt])

  useEffect(() => {
    if (visible) {
      fitTo(fitRef.current)
    }
  }, [visible])

  return (
    <div className="nk-ssh">
      <div ref={element} className="nk-ssh-terminal" />
      {state.kind === 'connecting' && (
        <div className="nk-ssh-overlay" role="status">
          Connecting…
        </div>
      )}
      {state.kind === 'ended' && (
        <div className="nk-ssh-overlay" role="status">
          <span>{state.message}</span>
          <button type="button" onClick={() => setAttempt((n) => n + 1)}>
            Reconnect
          </button>
        </div>
      )}
    </div>
  )
}
