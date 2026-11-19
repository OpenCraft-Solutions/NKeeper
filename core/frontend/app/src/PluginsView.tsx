import { useState } from 'react'
import { api } from '@nkeeper/sdk'
import type { Catalog } from './catalog'
import { PluginCatalog } from './PluginCatalog'
import { PluginIntegrations } from './PluginIntegrations'
import { errorMessage, usePluginAdmin, type PluginAdmin } from './pluginAdmin'
import type { FrontendState, PluginStatus } from './plugins'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import page from './ui/Page.module.css'
import report from './ui/Reports.module.css'

const STATE_LABELS: Record<PluginStatus['state'], string> = {
  loaded: 'Loaded',
  disabled: 'Disabled',
  awaitingSignerApproval: 'Awaiting signer approval',
  refused: 'Refused',
  missing: 'Missing',
}

/** What an administrator is asked to confirm: turning a plugin off (with what needs it), or removing it. */
type Asking = { kind: 'disable' | 'remove'; plugin: PluginStatus; dependants: string[] }

/**
 * 1.x's Settings page, for administrators (docs/architecture.md §7.14). Its plugins section lists the plugins the
 * server knows, with their states and whether their frontends loaded here, and turns them on and off (§9.2); below
 * it, the plugin catalog (§9.1, §9.3).
 */
export function PluginsView({ plugins, frontends, catalog }: { plugins: PluginStatus[]; frontends: Map<string, FrontendState>; catalog: Catalog }) {
  const { admin, setAdmin, error, setError } = usePluginAdmin()
  const [busy, setBusy] = useState<string | null>(null)
  const [asking, setAsking] = useState<Asking | null>(null)

  async function act(key: string, action: () => Promise<PluginAdmin>, fallback: string) {
    setBusy(key)
    setError(null)
    try {
      setAdmin(await action())
      setAsking(null)
    } catch (e) {
      setError(errorMessage(e, fallback))
    } finally {
      setBusy(null)
    }
  }

  const toggle = (plugin: PluginStatus, enable: boolean) =>
    act(
      plugin.id,
      async () => (await api.post<{ catalog: PluginAdmin }>(`/api/plugins/${encodeURIComponent(plugin.id)}/${enable ? 'enable' : 'disable'}`)).catalog,
      `Couldn’t turn ${plugin.id} ${enable ? 'on' : 'off'}.`,
    )

  const dependantsOf = (id: string) => plugins.filter((p) => p.dependsOn?.includes(id) && admin?.switches[p.id]).map((p) => p.id)
  const pendingRestart = admin ? plugins.some((p) => pending(p, admin) !== null) : false

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
          <p className={page.hint}>
            The plugins this server verified and loaded, and whether their part of the page loaded in this browser. Turning one on or off takes effect when
            NKeeper restarts.
          </p>
        </div>
        {error ? <Alert>{error}</Alert> : null}
        {pendingRestart ? (
          <Alert variant="warning">
            Some changes wait for NKeeper to restart.{' '}
            {admin?.canRequest ? (
              <Button
                size="sm"
                loading={busy === 'restart'}
                onClick={() => void act('restart', () => api.post<PluginAdmin>('/api/plugins/restart'), 'Couldn’t ask for a restart.')}
              >
                Restart now (ends every open session)
              </Button>
            ) : (
              'Root can restart it: sudo systemctl restart nkeeper'
            )}
          </Alert>
        ) : null}
        {asking ? (
          <Alert variant="warning">
            {asking.kind === 'disable' ? `Turn ${asking.plugin.id} off at the next restart?` : `Remove ${asking.plugin.id}? Its data is kept, and NKeeper restarts.`}
            {asking.dependants.length > 0 ? ` ${asking.dependants.join(', ')} need${asking.dependants.length === 1 ? 's' : ''} it and ${asking.kind === 'disable' ? 'turn' : 'must be removed'} ${asking.kind === 'disable' ? 'off too' : 'first'}.` : ''}{' '}
            <Button
              size="sm"
              variant="danger"
              disabled={asking.kind === 'remove' && asking.dependants.length > 0}
              loading={busy === asking.plugin.id}
              onClick={() =>
                void (asking.kind === 'disable'
                  ? toggle(asking.plugin, false)
                  : act(asking.plugin.id, () => api.post<PluginAdmin>(`/api/plugins/${encodeURIComponent(asking.plugin.id)}/remove`), `Couldn’t remove ${asking.plugin.id}.`))
              }
            >
              {asking.kind === 'disable' ? `Turn ${asking.plugin.id} off` : `Remove ${asking.plugin.id}`}
            </Button>{' '}
            <Button size="sm" variant="ghost" onClick={() => setAsking(null)}>
              Cancel
            </Button>
          </Alert>
        ) : null}
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
                {admin ? <th scope="col">Actions</th> : null}
              </tr>
            </thead>
            <tbody>
              {plugins.map((plugin) => {
                const on = admin?.switches[plugin.id]
                const change = admin ? pending(plugin, admin) : null
                return (
                  <tr key={plugin.id}>
                    <td>
                      {plugin.id}
                      {plugin.builtIn && ' (on by default)'}
                      {plugin.credentialRelease ? <p className={report.overcommitted}>Can see passwords: {plugin.credentialRelease}</p> : null}
                    </td>
                    <td className={report.mono}>{plugin.version ?? '—'}</td>
                    <td className={plugin.state === 'loaded' ? undefined : report.overcommitted}>
                      {STATE_LABELS[plugin.state]}
                      {plugin.reasonCode && ` (${plugin.reasonCode})`}
                      {change ? <p className={page.hint}>{change}</p> : null}
                    </td>
                    <td>{describeFrontend(frontends.get(plugin.id))}</td>
                    {admin ? (
                      <td>
                        {plugin.state !== 'missing' && on !== undefined ? (
                          on ? (
                            <Button size="sm" variant="secondary" loading={busy === plugin.id} onClick={() => setAsking({ kind: 'disable', plugin, dependants: dependantsOf(plugin.id) })}>
                              Turn off
                            </Button>
                          ) : (
                            <Button size="sm" variant="secondary" loading={busy === plugin.id} onClick={() => void toggle(plugin, true)}>
                              Turn on
                            </Button>
                          )
                        ) : null}{' '}
                        {admin.canRequest && plugin.state !== 'missing' ? (
                          <Button
                            size="sm"
                            variant="ghost"
                            disabled={admin.pending.includes(plugin.id)}
                            onClick={() => setAsking({ kind: 'remove', plugin, dependants: plugins.filter((p) => p.dependsOn?.includes(plugin.id)).map((p) => p.id) })}
                          >
                            Remove
                          </Button>
                        ) : null}
                      </td>
                    ) : null}
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </section>
      <PluginIntegrations catalog={catalog} />
      {admin ? <PluginCatalog admin={admin} onChange={setAdmin} /> : null}
    </div>
  )
}

/** A change recorded but not yet in effect: it waits for the next restart (§9.2). */
function pending(plugin: PluginStatus, admin: PluginAdmin): string | null {
  const on = admin.switches[plugin.id]
  if (on === true && plugin.state === 'disabled') {
    return 'On at the next restart'
  }
  if (on === false && plugin.state === 'loaded') {
    return 'Off at the next restart'
  }
  return null
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
