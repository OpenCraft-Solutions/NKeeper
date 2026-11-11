import { useEffect, useState } from 'react'
import { api, ApiError, type SessionViewProps, type TrustPrompt } from '@nkeeper/sdk'

/** The view's own styles, adopted when the plugin activates. */
export const webStyles = `
.nk-web { display: flex; flex-direction: column; height: 100%; min-height: 0; }
.nk-web iframe { flex: 1; width: 100%; border: 0; background: #fff; }
.nk-web-status { padding: 16px 20px; display: flex; gap: 12px; align-items: center; }
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

  if (state.kind === 'open') {
    return (
      <div className="nk-web">
        <iframe title={`${host.name} web page`} src={state.url} />
      </div>
    )
  }
  return (
    <div className="nk-web">
      <div className="nk-web-status" role="status">
        {state.kind === 'opening' ? (
          'Opening…'
        ) : (
          <>
            <span>{state.message}</span>
            <button
              type="button"
              onClick={() => {
                setState({ kind: 'opening' })
                setAttempt((n) => n + 1)
              }}
            >
              Try again
            </button>
          </>
        )}
      </div>
    </div>
  )
}
