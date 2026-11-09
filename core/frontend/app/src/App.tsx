import { useEffect, useState } from 'react'
import { api, ApiError, onUnauthorized, resetAntiforgeryToken } from '@nkeeper/sdk'
import { loadPluginFrontends, type FrontendState, type ModuleImporter, type PluginStatus } from './plugins'
import { SignIn } from './SignIn'

const STATE_LABELS: Record<PluginStatus['state'], string> = {
  loaded: 'Loaded',
  disabled: 'Disabled',
  awaitingSignerApproval: 'Awaiting signer approval',
  refused: 'Refused',
  missing: 'Missing',
}

type Load =
  | { kind: 'loading' }
  | { kind: 'signedOut' }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; plugins: PluginStatus[]; frontends: Map<string, FrontendState> }

/** The application shell: signs the user in, lists the server's plugins and loads their frontends. */
export function App({ importer }: { importer?: ModuleImporter }) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)

  // A session that ends (signed out elsewhere, expired, revoked) brings back the sign-in form.
  useEffect(() => onUnauthorized(() => setLoad({ kind: 'signedOut' })), [])

  useEffect(() => {
    let cancelled = false
    void (async () => {
      try {
        const plugins = await api.get<PluginStatus[]>('/api/plugins')
        const frontends = await loadPluginFrontends(plugins, importer)
        if (!cancelled) {
          setLoad({ kind: 'ready', plugins, frontends })
        }
      } catch (error) {
        if (!cancelled) {
          setLoad(
            error instanceof ApiError && error.status === 401
              ? { kind: 'signedOut' }
              : { kind: 'error', message: error instanceof ApiError ? error.message : 'The plugin list could not be loaded.' },
          )
        }
      }
    })()
    return () => {
      cancelled = true
    }
  }, [importer, attempt])

  async function signOut() {
    await api.post('/api/auth/logout').catch(() => undefined)
    resetAntiforgeryToken()
    setLoad({ kind: 'signedOut' })
  }

  return (
    <main>
      <h1>NKeeper</h1>
      <p>Version 2.0 is in early development.</p>
      {load.kind === 'signedOut' && <SignIn onSignedIn={() => setAttempt((n) => n + 1)} />}
      {load.kind !== 'signedOut' && <h2>Plugins</h2>}
      {load.kind === 'loading' && <p>Loading plugins…</p>}
      {load.kind === 'error' && <p role="alert">{load.message}</p>}
      {load.kind === 'ready' && load.plugins.length === 0 && <p>No plugins are installed.</p>}
      {load.kind === 'ready' && load.plugins.length > 0 && (
        <table>
          <thead>
            <tr>
              <th scope="col">Plugin</th>
              <th scope="col">Version</th>
              <th scope="col">State</th>
              <th scope="col">Frontend</th>
            </tr>
          </thead>
          <tbody>
            {load.plugins.map((plugin) => (
              <tr key={plugin.id}>
                <td>
                  {plugin.id}
                  {plugin.builtIn && ' (built in)'}
                </td>
                <td>{plugin.version ?? '—'}</td>
                <td>
                  {STATE_LABELS[plugin.state]}
                  {plugin.reasonCode && ` (${plugin.reasonCode})`}
                </td>
                <td>{describeFrontend(load.frontends.get(plugin.id))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {load.kind === 'ready' && (
        <p>
          <button type="button" onClick={() => void signOut()}>
            Sign out
          </button>
        </p>
      )}
    </main>
  )
}

function describeFrontend(state: FrontendState | undefined): string {
  switch (state?.kind) {
    case 'active':
      return 'Active'
    case 'failed':
      return `Failed: ${state.reason}`
    default:
      return '—'
  }
}
