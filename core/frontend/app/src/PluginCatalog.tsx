import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import page from './ui/Page.module.css'
import report from './ui/Reports.module.css'

interface CatalogCandidate {
  id: string
  name: string
  version: string
  packageSha256: string
  keyId: string
  builtIn: boolean
  dependsOn: string[]
}

interface CatalogPlugin {
  id: string
  repository: string
  summary: string | null
  installedVersion: string | null
  available: CatalogCandidate | null
  reasonCode: string | null
  message: string | null
}

interface CatalogResult {
  id: string
  version: string | null
  result: string
  message: string
  requestedBy: string | null
  at: string
}

export interface Catalog {
  enabled: boolean
  canRequest: boolean
  catalogUrl: string | null
  refreshedAt: string | null
  error: string | null
  warnings: string[]
  plugins: CatalogPlugin[]
  pending: string[]
  results: CatalogResult[]
}

const message = (error: unknown, fallback: string) => (error instanceof ApiError ? error.message : fallback)

/**
 * The plugin catalog on the plugins page (docs/architecture.md §9.1): updates for installed plugins and plugins
 * that aren't installed yet. Checking only looks; installing asks root's helper, which restarts NKeeper.
 */
export function PluginCatalog() {
  const [catalog, setCatalog] = useState<Catalog | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)
  const [asking, setAsking] = useState<CatalogPlugin | null>(null)
  const [requesting, setRequesting] = useState(false)

  const load = useCallback(() => {
    api.get<Catalog>('/api/plugins/catalog').then(setCatalog, (e: unknown) => setError(message(e, 'Couldn’t read the plugin catalog.')))
  }, [])

  useEffect(load, [load])

  // While a request waits for root's helper, look again now and then: NKeeper restarts when it's done.
  useEffect(() => {
    if (!catalog || catalog.pending.length === 0) {
      return
    }
    const timer = window.setInterval(load, 10_000)
    return () => window.clearInterval(timer)
  }, [catalog, load])

  async function check() {
    setChecking(true)
    setError(null)
    try {
      setCatalog(await api.post<Catalog>('/api/plugins/catalog/refresh'))
    } catch (e) {
      setError(message(e, 'Couldn’t check for updates.'))
    } finally {
      setChecking(false)
    }
  }

  async function request(plugin: CatalogPlugin) {
    setRequesting(true)
    setError(null)
    try {
      setCatalog(await api.post<Catalog>('/api/plugins/catalog/requests', { id: plugin.id }))
      setAsking(null)
    } catch (e) {
      setError(message(e, `Couldn’t ask for ${plugin.id}.`))
    } finally {
      setRequesting(false)
    }
  }

  if (!catalog) {
    return error ? <Alert>{error}</Alert> : null
  }

  if (!catalog.enabled) {
    return (
      <section className={page.panel} aria-label="Plugin catalog">
        <h2 className={page.panelTitle}>Plugin catalog</h2>
        <p className={page.hint}>The catalog is off on this server (Plugins:Catalog:Url), so plugins come only with NKeeper’s installer.</p>
      </section>
    )
  }

  const updates = catalog.plugins.filter((p) => p.installedVersion && p.available)
  const available = catalog.plugins.filter((p) => !p.installedVersion && p.available)
  const problems = catalog.plugins.filter((p) => p.reasonCode && !p.available)

  return (
    <section className={page.panel} aria-label="Plugin catalog">
      <div>
        <h2 className={page.panelTitle}>Plugin catalog</h2>
        <p className={page.hint}>
          Plugins from the catalog, verified as this server would load them.{' '}
          {catalog.refreshedAt ? `Last checked ${new Date(catalog.refreshedAt).toLocaleString()}.` : 'Not checked yet.'}
        </p>
      </div>
      <div>
        <Button variant="secondary" size="sm" loading={checking} onClick={() => void check()}>
          Check for updates
        </Button>
      </div>
      {error ? <Alert>{error}</Alert> : null}
      {catalog.error ? <Alert variant="warning">{catalog.error}</Alert> : null}
      {catalog.pending.length > 0 ? (
        <Alert variant="info">Waiting for the server to install {catalog.pending.join(', ')}. NKeeper restarts when it’s done.</Alert>
      ) : null}
      {catalog.results.map((r) => (
        <Alert key={`${r.id}-${r.at}`} variant={r.result.toLowerCase() === 'failed' ? 'error' : 'info'}>
          {r.message}
        </Alert>
      ))}
      {asking ? (
        <Alert variant="warning">
          {asking.installedVersion ? 'Upgrade' : 'Install'} {asking.id} {asking.available?.version}
          {asking.available?.dependsOn.length ? ` (with ${asking.available.dependsOn.join(', ')} if missing)` : ''}? NKeeper restarts to load it,
          which ends every open session.{' '}
          <Button size="sm" loading={requesting} onClick={() => void request(asking)}>
            {asking.installedVersion ? 'Upgrade' : 'Install'} and restart
          </Button>{' '}
          <Button size="sm" variant="ghost" onClick={() => setAsking(null)}>
            Cancel
          </Button>
        </Alert>
      ) : null}
      <CatalogTable title="Updates" plugins={updates} canRequest={catalog.canRequest} pending={catalog.pending} onAsk={setAsking} />
      <CatalogTable title="Available" plugins={available} canRequest={catalog.canRequest} pending={catalog.pending} onAsk={setAsking} />
      {problems.length > 0 ? (
        <details>
          <summary className={page.hint}>{problems.length === 1 ? '1 plugin' : `${problems.length} plugins`} with nothing this server can install</summary>
          <ul>
            {problems.map((p) => (
              <li key={p.id} className={page.hint}>
                {p.id}: {p.message} ({p.reasonCode})
              </li>
            ))}
          </ul>
        </details>
      ) : null}
    </section>
  )
}

function CatalogTable({
  title,
  plugins,
  canRequest,
  pending,
  onAsk,
}: {
  title: string
  plugins: CatalogPlugin[]
  canRequest: boolean
  pending: string[]
  onAsk: (plugin: CatalogPlugin) => void
}) {
  if (plugins.length === 0) {
    return null
  }

  return (
    <table className={report.healthTable} aria-label={title}>
      <caption className={page.hint}>{title}</caption>
      <thead>
        <tr>
          <th scope="col">Plugin</th>
          <th scope="col">Version</th>
          <th scope="col">Repository</th>
          <th scope="col">{canRequest ? 'Action' : 'As root'}</th>
        </tr>
      </thead>
      <tbody>
        {plugins.map((p) => (
          <tr key={p.id}>
            <td>
              {p.available?.name ?? p.id}
              {p.summary ? <p className={page.hint}>{p.summary}</p> : null}
            </td>
            <td className={report.mono}>
              {p.installedVersion ? `${p.installedVersion} → ` : ''}
              {p.available?.version}
            </td>
            <td className={report.mono}>{p.repository}</td>
            <td>
              {canRequest ? (
                <Button size="sm" variant="secondary" disabled={pending.includes(p.id)} onClick={() => onAsk(p)}>
                  {pending.includes(p.id) ? 'Waiting…' : p.installedVersion ? 'Upgrade' : 'Install'}
                </Button>
              ) : (
                <code className={report.mono}>
                  sudo nkeeper plugin {p.installedVersion ? 'upgrade' : 'install'} {p.id}
                </code>
              )}
            </td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
