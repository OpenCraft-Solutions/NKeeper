import type { FrontendState, PluginStatus } from './plugins'

const STATE_LABELS: Record<PluginStatus['state'], string> = {
  loaded: 'Loaded',
  disabled: 'Disabled',
  awaitingSignerApproval: 'Awaiting signer approval',
  refused: 'Refused',
  missing: 'Missing',
}

/** The plugins the server knows, with their states and whether their frontends loaded here (Phase 0's list). */
export function PluginsView({ plugins, frontends }: { plugins: PluginStatus[]; frontends: Map<string, FrontendState> }) {
  return (
    <section className="nk-pad nk-scroll" aria-label="Plugins">
      <h2>Plugins</h2>
      {plugins.length === 0 ? (
        <p>No plugins are installed.</p>
      ) : (
        <table className="nk-table">
          <thead>
            <tr>
              <th scope="col">Plugin</th>
              <th scope="col">Version</th>
              <th scope="col">State</th>
              <th scope="col">Frontend</th>
            </tr>
          </thead>
          <tbody>
            {plugins.map((plugin) => (
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
                <td>{describeFrontend(frontends.get(plugin.id))}</td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </section>
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
