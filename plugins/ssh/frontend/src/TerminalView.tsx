import { useEffect, useRef, useState } from 'react'
import { SessionError, mirrorInjectedStyles, type SessionViewProps, type StreamConnection } from '@nkeeper/sdk'
import { FitAddon } from '@xterm/addon-fit'
import { Terminal } from '@xterm/xterm'

/**
 * The view's own styles, adopted when the plugin activates: 1.x's session look (a status bar over the terminal,
 * and cards for opening and failing), on the shell's design tokens, each with a fallback (docs/architecture.md §7.14).
 */
export const terminalStyles = `
.nk-ssh { display: flex; flex-direction: column; height: 100%; min-height: 0; background: var(--color-console-bg, #0b1220);
  color: var(--color-text, #e5ebf5); font-family: var(--font-sans, system-ui, sans-serif); }
.nk-ssh-status { display: flex; align-items: center; gap: 0.5rem; min-height: 2.25rem; padding: 0.25rem 1rem;
  background: var(--color-surface, #111a2e); border-bottom: 1px solid var(--color-border, #233150); font-size: 0.8125rem; }
.nk-ssh-dot { width: 0.5rem; height: 0.5rem; flex-shrink: 0; border-radius: 999px; background: var(--color-text-subtle, #7d8ba6); }
.nk-ssh-dot[data-state='connecting'] { background: var(--color-warning, #f5b84b); }
.nk-ssh-dot[data-state='open'] { background: #4ade80; }
.nk-ssh-dot[data-state='ended'] { background: #f87171; }
.nk-ssh-status-text { font-weight: 550; }
.nk-ssh-target { overflow: hidden; color: var(--color-text-muted, #9aa8c1); font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
  text-overflow: ellipsis; white-space: nowrap; }
.nk-ssh-actions { display: flex; align-items: center; gap: 0.5rem; margin-left: auto; }
.nk-ssh-area { position: relative; flex: 1; min-height: 0; }
.nk-ssh-terminal { position: absolute; inset: 0.5rem 0 0 0.75rem; }
.nk-ssh-terminal .xterm-viewport { scrollbar-color: var(--color-border-strong, #33456b) transparent; }
.nk-ssh-overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 1rem;
  background: rgb(11 18 32 / 0.75); }
.nk-ssh-card { display: flex; flex-direction: column; gap: 0.75rem; max-width: 32rem; padding: 1.25rem 1.5rem;
  background: var(--color-surface, #111a2e); border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-lg, 12px);
  box-shadow: var(--shadow-card, 0 1px 2px rgb(0 0 0 / 0.3), 0 8px 24px rgb(0 0 0 / 0.25)); font-size: 0.9375rem; }
.nk-ssh-card[data-error] { border-color: var(--color-danger-border, #b91c1c); }
.nk-ssh-card-row { flex-direction: row; align-items: center; }
.nk-ssh-card h3 { margin: 0; font-size: 1.0625rem; font-weight: 620; }
.nk-ssh-card p { margin: 0; }
.nk-ssh-card-actions { display: flex; flex-wrap: wrap; align-items: center; justify-content: flex-end; gap: 0.5rem; }
.nk-ssh-spinner { width: 1rem; height: 1rem; border: 2px solid var(--color-border-strong, #33456b); border-top-color: var(--color-accent-text, #8fb4ff);
  border-radius: 999px; animation: nk-ssh-spin 0.8s linear infinite; }
@keyframes nk-ssh-spin { to { transform: rotate(360deg); } }
.nk-ssh-button { display: inline-flex; align-items: center; justify-content: center; min-height: 2rem; padding: 0 0.75rem;
  border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-md, 8px); background: transparent;
  color: var(--color-text, #e5ebf5); font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; }
.nk-ssh-button:hover { background: var(--color-surface-raised, #16213a); border-color: var(--color-border-hover, #46598a); }
.nk-ssh-button[data-primary] { background: var(--color-accent, #2563eb); border-color: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); }
.nk-ssh-button[data-primary]:hover { background: var(--color-accent-hover, #1d4ed8); }
`

/** 1.x's terminal colours. */
const THEME = {
  background: '#0b1220',
  foreground: '#e5ebf5',
  cursor: '#8fb4ff',
  cursorAccent: '#0b1220',
  selectionBackground: 'rgba(37, 99, 235, 0.4)',
}

/** Ended: why, and whether it failed to open (a card) or ended after it opened (the status bar says so). */
type State = { kind: 'connecting' } | { kind: 'open' } | { kind: 'ended'; message: string; failed: boolean }

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
export function TerminalView({ connect, visible, host }: SessionViewProps) {
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
      fontFamily: "ui-monospace, 'Cascadia Mono', Consolas, 'DejaVu Sans Mono', monospace",
      fontSize: 14,
      scrollback: 5000,
      theme: THEME,
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
            setState({ kind: 'ended', message: END_MESSAGES[reason] ?? END_MESSAGES.closed!, failed: false })
          }
        })
      },
      (error: unknown) => {
        if (!disposed) {
          setState({ kind: 'ended', message: error instanceof SessionError ? error.message : 'The terminal could not be opened.', failed: true })
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

  const reconnect = () => setAttempt((n) => n + 1)
  return (
    <div className="nk-ssh">
      <div className="nk-ssh-status" role="status">
        <span className="nk-ssh-dot" data-state={state.kind} />
        <span className="nk-ssh-status-text">{state.kind === 'open' ? 'Connected' : state.kind === 'ended' ? 'Not connected' : 'Connecting…'}</span>
        <span className="nk-ssh-target">{host.name}</span>
        <div className="nk-ssh-actions">
          {state.kind === 'ended' && !state.failed && (
            <>
              <span>{state.message}</span>
              <button type="button" className="nk-ssh-button" onClick={reconnect}>
                Reconnect
              </button>
            </>
          )}
        </div>
      </div>
      <div className="nk-ssh-area">
        <div ref={element} className="nk-ssh-terminal" />
        {state.kind === 'connecting' && (
          <div className="nk-ssh-overlay">
            <div className="nk-ssh-card nk-ssh-card-row">
              <span className="nk-ssh-spinner" aria-hidden="true" /> Opening session…
            </div>
          </div>
        )}
        {state.kind === 'ended' && state.failed && (
          <div className="nk-ssh-overlay">
            <div className="nk-ssh-card" data-error role="alert">
              <h3>Couldn’t connect</h3>
              <p>{state.message}</p>
              <div className="nk-ssh-card-actions">
                <button type="button" className="nk-ssh-button" data-primary onClick={reconnect}>
                  Try again
                </button>
              </div>
            </div>
          </div>
        )}
      </div>
    </div>
  )
}
