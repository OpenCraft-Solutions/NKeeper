import { useEffect, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { loadPluginFrontends, type FrontendState, type ModuleImporter, type PluginStatus } from './plugins'

const STATE_LABELS: Record<PluginStatus['state'], string> = {
  loaded: 'Loaded',
  disabled: 'Disabled',
  awaitingSignerApproval: 'Awaiting signer approval',
  refused: 'Refused',
  missing: 'Missing',
}

type Load = { kind: 'loading' } | { kind: 'error'; message: string } | { kind: 'ready'; plugins: PluginStatus[]; frontends: Map<string, FrontendState> }

/** The application shell (Phase 0): lists the server's plugins and loads their frontends. */
export function App({ importer }: { importer?: ModuleImporter }) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' })

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
          setLoad({ kind: 'error', message: error instanceof ApiError ? error.message : 'The plugin list could not be loaded.' })
        }
      }
    })()
    return () => {
      cancelled = true
    }
  }, [importer])

  return (
    <main>
      <h1>NKeeper</h1>
      <p>Version 2.0 is in early development.</p>
      <h2>Plugins</h2>
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
