import type { FrontendState, PluginStatus } from './plugins'
import page from './ui/Page.module.css'
import report from './ui/Reports.module.css'

const STATE_LABELS: Record<PluginStatus['state'], string> = {
  loaded: 'Loaded',
  disabled: 'Disabled',
  awaitingSignerApproval: 'Awaiting signer approval',
  refused: 'Refused',
  missing: 'Missing',
}

/**
 * 1.x's Settings page, for administrators (docs/architecture.md §7.14). Its one section so far is the plugins the
 * server knows, with their states and whether their frontends loaded here (Phase 0's list).
 */
export function PluginsView({ plugins, frontends }: { plugins: PluginStatus[]; frontends: Map<string, FrontendState> }) {
  return (
    <div className={page.page}>
      <header className={page.header}>
        <h1 className={page.title}>Settings</h1>
        <p className={page.lead}>Settings for the whole NKeeper instance. Only administrators see this page.</p>
      </header>
      <section className={page.panel} aria-labelledby="plugins-title" aria-label="Plugins">
        <div>
          <h2 id="plugins-title" className={page.panelTitle}>
            Plugins
          </h2>
          <p className={page.hint}>The plugins this server verified and loaded, and whether their part of the page loaded in this browser.</p>
        </div>
        {plugins.length === 0 ? (
          <p className={page.hint}>No plugins are installed.</p>
        ) : (
          <table className={report.healthTable}>
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
                  <td className={report.mono}>{plugin.version ?? '—'}</td>
                  <td className={plugin.state === 'loaded' ? undefined : report.overcommitted}>
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
    </div>
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
