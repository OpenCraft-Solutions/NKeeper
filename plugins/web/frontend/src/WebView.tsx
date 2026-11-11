import { useEffect, useState } from 'react'
import { api, ApiError, type SessionViewProps, type TrustPrompt } from '@nkeeper/sdk'

/** The view's own styles, adopted when the plugin activates. */
export const webStyles = `
.nk-web { display: flex; flex-direction: column; height: 100%; min-height: 0; background: var(--color-bg, #0b1220);
  color: var(--color-text, #e5ebf5); font-family: var(--font-sans, system-ui, sans-serif); }
.nk-web-status { display: flex; align-items: center; gap: 0.5rem; min-height: 2.25rem; padding: 0.25rem 1rem;
  background: var(--color-surface, #111a2e); border-bottom: 1px solid var(--color-border, #233150); font-size: 0.8125rem; }
.nk-web-dot { width: 0.5rem; height: 0.5rem; flex-shrink: 0; border-radius: 999px; background: var(--color-warning, #f5b84b); }
.nk-web-dot[data-state='open'] { background: #4ade80; }
.nk-web-dot[data-state='failed'] { background: #f87171; }
.nk-web-status-text { font-weight: 550; }
.nk-web-target { overflow: hidden; color: var(--color-text-muted, #9aa8c1); font-family: ui-monospace, 'Cascadia Mono', Consolas, monospace;
  text-overflow: ellipsis; white-space: nowrap; }
.nk-web-area { position: relative; flex: 1; min-height: 0; }
.nk-web-area iframe { position: absolute; inset: 0; width: 100%; height: 100%; border: 0; background: #fff; }
.nk-web-overlay { position: absolute; inset: 0; display: flex; align-items: center; justify-content: center; padding: 1rem;
  background: rgb(11 18 32 / 0.75); }
.nk-web-card { display: flex; flex-direction: column; gap: 0.75rem; max-width: 32rem; padding: 1.25rem 1.5rem;
  background: var(--color-surface, #111a2e); border: 1px solid var(--color-border-strong, #33456b); border-radius: var(--radius-lg, 12px);
  box-shadow: var(--shadow-card, 0 1px 2px rgb(0 0 0 / 0.3), 0 8px 24px rgb(0 0 0 / 0.25)); font-size: 0.9375rem; }
.nk-web-card[data-error] { border-color: var(--color-danger-border, #b91c1c); }
.nk-web-card-row { flex-direction: row; align-items: center; }
.nk-web-card h3 { margin: 0; font-size: 1.0625rem; font-weight: 620; }
.nk-web-card p { margin: 0; }
.nk-web-card-actions { display: flex; justify-content: flex-end; gap: 0.5rem; }
.nk-web-spinner { width: 1rem; height: 1rem; border: 2px solid var(--color-border-strong, #33456b); border-top-color: var(--color-accent-text, #8fb4ff);
  border-radius: 999px; animation: nk-web-spin 0.8s linear infinite; }
@keyframes nk-web-spin { to { transform: rotate(360deg); } }
.nk-web-button { display: inline-flex; align-items: center; min-height: 2rem; padding: 0 0.75rem; border: 0; border-radius: var(--radius-md, 8px);
  background: var(--color-accent, #2563eb); color: var(--color-on-accent, #fff); font: inherit; font-size: 0.8125rem; font-weight: 600; cursor: pointer; }
.nk-web-button:hover { background: var(--color-accent-hover, #1d4ed8); }
`

/** How often an open tab tells the plugin the session is still in use, though the site itself may be idle. */
const TOUCH_MS = 60_000

type State = { kind: 'opening' } | { kind: 'open'; id: string; url: string } | { kind: 'failed'; message: string }

interface Opened {
  id: string
  url: string
}

/**
 * The web session type's tab (docs/architecture.md §7.19): opens the session through the plugin's endpoint, asks
 * core to trust a certificate when it must, and shows the site in a frame on its own origin.
 */
export function WebView({ host, serviceId, confirmTrust }: SessionViewProps) {
  const [state, setState] = useState<State>({ kind: 'opening' })
  const [attempt, setAttempt] = useState(0)
  const base = `/api/p/web/hosts/${host.id}/sessions`

  useEffect(() => {
    let cancelled = false
    let opened: string | null = null
    let touch: ReturnType<typeof setInterval> | undefined

    async function open(): Promise<void> {
      try {
        const session = await api.post<Opened>(base, { serviceId })
        if (cancelled) {
          void api.delete(`${base}/${session.id}`).catch(() => undefined)
          return
        }
        opened = session.id
        setState({ kind: 'open', ...session })
        touch = setInterval(() => void api.post(`${base}/${session.id}/touch`).catch(() => undefined), TOUCH_MS)
      } catch (error) {
        // Core's presented identity is a trust prompt as it stands (SDK 1.0).
        const prompt = error instanceof ApiError && error.code === 'host_key_unknown' ? (error.problem as { presented?: TrustPrompt } | undefined)?.presented : undefined
        if (prompt && !cancelled) {
          if (await confirmTrust(prompt)) {
            return open()
          }
          setState({ kind: 'failed', message: 'The server’s certificate isn’t trusted, so the page didn’t open. Someone who can manage the host can trust it.' })
          return
        }
        if (!cancelled) {
          setState({ kind: 'failed', message: error instanceof ApiError ? error.message : 'The page couldn’t be opened.' })
        }
      }
    }

    void open()
    return () => {
      cancelled = true
      clearInterval(touch)
      if (opened) {
        void api.delete(`${base}/${opened}`).catch(() => undefined)
      }
    }
  }, [base, serviceId, confirmTrust, attempt])

  const status = state.kind === 'open' ? 'Connected' : state.kind === 'opening' ? 'Connecting…' : 'Not connected'
  return (
    <div className="nk-web">
      <div className="nk-web-status" role="status">
        <span className="nk-web-dot" data-state={state.kind} />
        <span className="nk-web-status-text">{status}</span>
        <span className="nk-web-target">{host.name}</span>
      </div>
      <div className="nk-web-area">
        {state.kind === 'open' && <iframe title={`${host.name} web page`} src={state.url} />}
        {state.kind === 'opening' && (
          <div className="nk-web-overlay">
            <div className="nk-web-card nk-web-card-row">
              <span className="nk-web-spinner" aria-hidden="true" /> Opening session…
            </div>
          </div>
        )}
        {state.kind === 'failed' && (
          <div className="nk-web-overlay">
            <div className="nk-web-card" data-error role="alert">
              <h3>Couldn’t connect</h3>
              <p>{state.message}</p>
              <div className="nk-web-card-actions">
                <button
                  type="button"
                  className="nk-web-button"
                  onClick={() => {
                    setState({ kind: 'opening' })
                    setAttempt((n) => n + 1)
                  }}
                >
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
