import { useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { errorMessage, type CatalogListing, type PluginAdmin, type UploadResponse } from './pluginAdmin'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import page from './ui/Page.module.css'
import report from './ui/Reports.module.css'

/**
 * The plugin catalogs on the plugins page (docs/architecture.md §9.1, §9.3): updates, plugins to install, catalogs,
 * uploads and trusted keys. Checking only looks; installing asks root's helper, which restarts NKeeper.
 */
export function PluginCatalog({ admin, onChange }: { admin: PluginAdmin; onChange: (admin: PluginAdmin) => void }) {
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState<string | null>(null)
  const [asking, setAsking] = useState<CatalogListing | null>(null)

  async function run(key: string, action: () => Promise<PluginAdmin | void>, fallback: string) {
    setBusy(key)
    setError(null)
    setNotice(null)
    try {
      const next = await action()
      if (next) {
        onChange(next)
      }
    } catch (e) {
      setError(errorMessage(e, fallback))
    } finally {
      setBusy(null)
    }
  }

  const updates = admin.plugins.filter((p) => p.status.installedVersion && p.status.available)
  const available = admin.plugins.filter((p) => !p.status.installedVersion && p.status.available)
  const problems = admin.plugins.filter((p) => p.status.reasonCode && !p.status.available)

  return (
    <section className={page.panel} aria-label="Plugin catalog">
      <div>
        <h2 className={page.panelTitle}>Plugin catalog</h2>
        <p className={page.hint}>
          Plugins from the catalogs, verified as this server would load them.{' '}
          {admin.refreshedAt ? `Last checked ${new Date(admin.refreshedAt).toLocaleString()}.` : 'Not checked yet.'}
        </p>
      </div>
      <div>
        <Button
          variant="secondary"
          size="sm"
          loading={busy === 'refresh'}
          onClick={() => void run('refresh', () => api.post<PluginAdmin>('/api/plugins/catalog/refresh'), 'Couldn’t check for updates.')}
        >
          Check for updates
        </Button>
      </div>
      {error ? <Alert>{error}</Alert> : null}
      {notice ? <Alert variant="info">{notice}</Alert> : null}
      {admin.catalogs
        .filter((c) => c.error)
        .map((c) => (
          <Alert key={c.url} variant="warning">
            {c.url}: {c.error}
          </Alert>
        ))}
      {admin.pending.length > 0 ? (
        <Alert variant="info">Waiting for the server to apply: {admin.pending.join(', ')}. NKeeper restarts when it’s done.</Alert>
      ) : null}
      {admin.results.map((r) => (
        <Alert key={`${r.id}-${r.at}`} variant={r.result.toLowerCase() === 'failed' ? 'error' : 'info'}>
          {r.message}
        </Alert>
      ))}
      {asking?.status.available ? (
        <Alert variant="warning">
          {asking.status.installedVersion ? 'Upgrade' : 'Install'} {asking.status.id} {asking.status.available.version}
          {asking.status.available.dependsOn.length ? ` (with ${asking.status.available.dependsOn.join(', ')} if missing)` : ''}
          {asking.official ? '' : ` from ${asking.catalog}`}? NKeeper restarts to load it, which ends every open session.{' '}
          <Button
            size="sm"
            loading={busy === 'install'}
            onClick={() =>
              void run(
                'install',
                async () => {
                  const next = await api.post<PluginAdmin>('/api/plugins/catalog/requests', { id: asking.status.id })
                  setAsking(null)
                  return next
                },
                `Couldn’t ask for ${asking.status.id}.`,
              )
            }
          >
            {asking.status.installedVersion ? 'Upgrade' : 'Install'} and restart
          </Button>{' '}
          <Button size="sm" variant="ghost" onClick={() => setAsking(null)}>
            Cancel
          </Button>
        </Alert>
      ) : null}
      {admin.enabled || admin.catalogs.length > 0 ? (
        <>
          <CatalogTable title="Updates" plugins={updates} admin={admin} onAsk={setAsking} />
          <CatalogTable title="Available" plugins={available} admin={admin} onAsk={setAsking} />
        </>
      ) : (
        <p className={page.hint}>NKeeper’s catalog is off on this server (Plugins:Catalog:Url).</p>
      )}
      {problems.length > 0 ? (
        <details>
          <summary className={page.hint}>{problems.length === 1 ? '1 plugin' : `${problems.length} plugins`} with nothing this server can install</summary>
          <ul>
            {problems.map((p) => (
              <li key={p.status.id} className={page.hint}>
                {p.status.id}: {p.status.message} ({p.status.reasonCode})
              </li>
            ))}
          </ul>
        </details>
      ) : null}
      {admin.conflicts.length > 0 ? (
        <details>
          <summary className={page.hint}>Plugins more than one catalog lists</summary>
          <ul>
            {admin.conflicts.map((c) => (
              <li key={c} className={page.hint}>
                {c}
              </li>
            ))}
          </ul>
        </details>
      ) : null}
      <Catalogs admin={admin} busy={busy} run={run} />
      <Upload admin={admin} onNotice={setNotice} run={run} busy={busy} />
      <TrustedKeys admin={admin} busy={busy} run={run} />
    </section>
  )
}

type Run = (key: string, action: () => Promise<PluginAdmin | void>, fallback: string) => Promise<void>

function CatalogTable({ title, plugins, admin, onAsk }: { title: string; plugins: CatalogListing[]; admin: PluginAdmin; onAsk: (plugin: CatalogListing) => void }) {
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
          <th scope="col">From</th>
          <th scope="col">{admin.canRequest ? 'Action' : 'As root'}</th>
        </tr>
      </thead>
      <tbody>
        {plugins.map((p) => {
          const { status } = p
          const waiting = admin.pending.includes(status.id)
          return (
            <tr key={status.id}>
              <td>
                {status.available?.name ?? status.id}
                {status.summary ? <p className={page.hint}>{status.summary}</p> : null}
              </td>
              <td className={report.mono}>
                {status.installedVersion ? `${status.installedVersion} → ` : ''}
                {status.available?.version}
              </td>
              <td className={report.mono}>
                {status.repository}
                {p.official ? null : <p className={page.hint}>{p.catalog}</p>}
              </td>
              <td>
                {admin.canRequest ? (
                  <Button size="sm" variant="secondary" disabled={waiting} onClick={() => onAsk(p)}>
                    {waiting ? 'Waiting…' : status.installedVersion ? 'Upgrade' : 'Install'}
                  </Button>
                ) : (
                  <code className={report.mono}>
                    sudo nkeeper plugin {status.installedVersion ? 'upgrade' : 'install'} {status.id}
                  </code>
                )}
              </td>
            </tr>
          )
        })}
      </tbody>
    </table>
  )
}

/** Publishers' catalogs (§9.3): adding one grants nothing by itself; its packages still need a trusted key. */
function Catalogs({ admin, busy, run }: { admin: PluginAdmin; busy: string | null; run: Run }) {
  const [url, setUrl] = useState('')
  const added = admin.catalogs.filter((c) => !c.official)
  return (
    <details>
      <summary className={page.hint}>Catalogs ({added.length} added)</summary>
      <ul>
        {admin.catalogs.map((c) => (
          <li key={c.url} className={page.hint}>
            <span className={report.mono}>{c.url}</span>
            {c.official ? ' (NKeeper’s)' : null}{' '}
            {c.official ? null : (
              <Button
                size="sm"
                variant="ghost"
                loading={busy === `catalog-${c.url}`}
                onClick={() => void run(`catalog-${c.url}`, () => api.delete<PluginAdmin>(`/api/plugins/catalog/catalogs?url=${encodeURIComponent(c.url)}`), 'Couldn’t remove the catalog.')}
              >
                Remove
              </Button>
            )}
          </li>
        ))}
      </ul>
      <form
        onSubmit={(e) => {
          e.preventDefault()
          void run(
            'catalog-add',
            async () => {
              const next = await api.post<PluginAdmin>('/api/plugins/catalog/catalogs', { url })
              setUrl('')
              return next
            },
            'Couldn’t add the catalog.',
          )
        }}
      >
        <label className={page.hint}>
          A publisher’s catalog (https://…){' '}
          <input type="url" required value={url} onChange={(e) => setUrl(e.target.value)} aria-label="Catalog URL" />
        </label>{' '}
        <Button type="submit" size="sm" variant="secondary" loading={busy === 'catalog-add'}>
          Add catalog
        </Button>
      </form>
    </details>
  )
}

/** Uploading a package (§9.3): verified here, installed by root's helper only if root's trust accepts it too. */
function Upload({ admin, busy, run, onNotice }: { admin: PluginAdmin; busy: string | null; run: Run; onNotice: (text: string) => void }) {
  const [file, setFile] = useState<File | null>(null)
  if (!admin.canRequest) {
    return null
  }

  async function upload(chosen: File): Promise<void> {
    const form = new FormData()
    form.append('package', chosen, chosen.name)
    try {
      onNotice((await api.post<UploadResponse>('/api/plugins/upload', form)).message)
    } catch (e) {
      // A package that doesn't verify comes back with why, as the upload's own answer.
      const refused = e instanceof ApiError ? (e.problem as unknown as UploadResponse | undefined) : undefined
      if (refused?.reasonCode) {
        throw new ApiError(422, `${refused.message} (${refused.reasonCode})`)
      }
      throw e
    }
  }

  return (
    <details>
      <summary className={page.hint}>Upload a plugin package</summary>
      <p className={page.hint}>
        A .nkp package, signed by a key this server trusts. The server checks it, then installs it and restarts, which ends every open session.
      </p>
      <input type="file" accept=".nkp" aria-label="Plugin package" onChange={(e) => setFile(e.target.files?.[0] ?? null)} />{' '}
      <Button size="sm" variant="secondary" disabled={!file} loading={busy === 'upload'} onClick={() => file && void run('upload', () => upload(file), 'Couldn’t upload the package.')}>
        Upload and install
      </Button>
    </details>
  )
}

/** Third-party keys (§9.3): listed always; trusted and untrusted here only where root allowed it. */
function TrustedKeys({ admin, busy, run }: { admin: PluginAdmin; busy: string | null; run: Run }) {
  const [keyFile, setKeyFile] = useState('')
  const [password, setPassword] = useState('')
  return (
    <details>
      <summary className={page.hint}>Trusted publisher keys ({admin.keys.length})</summary>
      <ul>
        {admin.keys.map((k) => (
          <li key={k.keyId} className={page.hint}>
            <span className={report.mono}>{k.keyId}</span>: {k.allowedIds.join(', ')}
            {k.comment ? ` (${k.comment})` : ''}
            {admin.canTrust ? (
              <>
                {' '}
                <Button
                  size="sm"
                  variant="ghost"
                  disabled={!password}
                  loading={busy === `untrust-${k.keyId}`}
                  onClick={() => void run(`untrust-${k.keyId}`, () => api.post<PluginAdmin>('/api/plugins/untrust', { keyId: k.keyId, password }), 'Couldn’t untrust the key.')}
                >
                  Untrust
                </Button>
              </>
            ) : null}
          </li>
        ))}
      </ul>
      {admin.canTrust ? (
        <form
          onSubmit={(e) => {
            e.preventDefault()
            void run(
              'trust',
              async () => {
                const next = await api.post<PluginAdmin>('/api/plugins/trust', { keyFile, password })
                setKeyFile('')
                return next
              },
              'Couldn’t trust the key.',
            )
          }}
        >
          <p className={page.hint}>
            Paste the publisher’s key file. Check its key ID and the plugin IDs it may sign against what the publisher published: a trusted key’s plugins run with
            this server’s access.
          </p>
          <textarea aria-label="Key file" rows={4} cols={60} value={keyFile} onChange={(e) => setKeyFile(e.target.value)} required />
          <br />
          <label className={page.hint}>
            Your password <input type="password" aria-label="Your password" value={password} onChange={(e) => setPassword(e.target.value)} required />
          </label>{' '}
          <Button type="submit" size="sm" variant="secondary" loading={busy === 'trust'}>
            Trust key
          </Button>
        </form>
      ) : (
        <p className={page.hint}>Root trusts a publisher’s key on the server: sudo nkeeper plugin trust add &lt;key file&gt;</p>
      )}
    </details>
  )
}
