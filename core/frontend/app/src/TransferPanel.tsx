import { useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import page from './ui/Page.module.css'

/** What an export file holds, from `POST /api/transfer/import/preview` (docs/architecture.md §7.45). */
interface ImportPreview {
  exportedAtUtc: string
  exportedBy: string
  nKeeperVersion: string
  users: number
  hosts: number
  credentials: number
  missingPlugins: string[]
  warnings: string[]
}

const MIN_PASSPHRASE = 16

function messageOf(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    const errors = (error.problem as { errors?: Record<string, string[]> } | undefined)?.errors
    return Object.values(errors ?? {})[0]?.[0] ?? (error.message || fallback)
  }
  return fallback
}

/**
 * Settings ▸ Move this instance (docs/architecture.md §7.45), ported from 1.x's transfer page: export everything into a
 * file encrypted with a passphrase, or import one into this fresh instance.
 */
export function TransferPanel() {
  return (
    <section className={page.panel} aria-label="Move this instance">
      <div>
        <h2 className={page.panelTitle}>Move this instance</h2>
        <p className={page.hint}>
          Move every user, host, credential and plugin setting to a new NKeeper server, in one file encrypted with a passphrase. Credentials are encrypted again
          with the new server’s own key. Plugins aren’t in the file: install them on the new server from its catalog.
        </p>
      </div>
      <Export />
      <Import />
    </section>
  )
}

function Export() {
  const [password, setPassword] = useState('')
  const [passphrase, setPassphrase] = useState('')
  const [confirmation, setConfirmation] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [done, setDone] = useState(false)

  async function exportNow() {
    setBusy(true)
    setError(null)
    setDone(false)
    try {
      const blob = await api.post<Blob>('/api/transfer/export', { password, passphrase, passphraseConfirmation: confirmation }, { responseType: 'blob' })
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `nkeeper-export-${new Date().toISOString().slice(0, 10)}.nkeeper`
      link.click()
      URL.revokeObjectURL(url)
      setPassword('')
      setDone(true)
    } catch (e) {
      setError(messageOf(e, 'The export failed.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <form
      aria-label="Export"
      onSubmit={(e) => {
        e.preventDefault()
        void exportNow()
      }}
    >
      <h3 className={page.legend}>Export</h3>
      <p className={page.hint}>
        The file holds every credential’s secret, protected only by the passphrase: choose a long one, keep the file safe, and delete it after the move.
      </p>
      {error ? <Alert>{error}</Alert> : null}
      {done ? <Alert variant="info">Exported. Import the file on the new server, with this passphrase.</Alert> : null}
      <label className={page.hint}>
        Your password <input type="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} />
      </label>{' '}
      <label className={page.hint}>
        Passphrase <input type="password" autoComplete="new-password" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
      </label>{' '}
      <label className={page.hint}>
        Passphrase again <input type="password" autoComplete="new-password" value={confirmation} onChange={(e) => setConfirmation(e.target.value)} />
      </label>{' '}
      <Button type="submit" size="sm" variant="secondary" loading={busy} disabled={!password || passphrase.length < MIN_PASSPHRASE || passphrase !== confirmation}>
        Export
      </Button>
      {passphrase.length > 0 && passphrase.length < MIN_PASSPHRASE ? <p className={page.hint}>The passphrase needs at least {MIN_PASSPHRASE} characters.</p> : null}
    </form>
  )
}

function Import() {
  const [file, setFile] = useState<File | null>(null)
  const [passphrase, setPassphrase] = useState('')
  const [preview, setPreview] = useState<ImportPreview | null>(null)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const form = (confirm: boolean) => {
    const body = new FormData()
    body.append('file', file!, file!.name)
    body.append('passphrase', passphrase)
    if (confirm) {
      body.append('confirm', 'true')
    }
    return body
  }

  async function check() {
    setBusy(true)
    setError(null)
    setPreview(null)
    try {
      setPreview(await api.post<ImportPreview>('/api/transfer/import/preview', form(false)))
    } catch (e) {
      setError(messageOf(e, 'The file couldn’t be read.'))
    } finally {
      setBusy(false)
    }
  }

  async function importNow() {
    setBusy(true)
    setError(null)
    try {
      await api.post('/api/transfer/import', form(true))
      // Every session ended with the import: sign in again, with an account from the old server.
      window.location.reload()
    } catch (e) {
      setError(messageOf(e, 'The import failed.'))
      setBusy(false)
    }
  }

  return (
    <form
      aria-label="Import"
      onSubmit={(e) => {
        e.preventDefault()
        void check()
      }}
    >
      <h3 className={page.legend}>Import</h3>
      <p className={page.hint}>Only into a fresh NKeeper, with no hosts and no users but you. Everything here is replaced, and you sign in again with your account from the old server.</p>
      {error ? <Alert>{error}</Alert> : null}
      <label className={page.hint}>
        Export file{' '}
        <input
          type="file"
          accept=".nkeeper"
          onChange={(e) => {
            setFile(e.target.files?.[0] ?? null)
            setPreview(null)
          }}
        />
      </label>{' '}
      <label className={page.hint}>
        Passphrase <input type="password" autoComplete="off" value={passphrase} onChange={(e) => setPassphrase(e.target.value)} />
      </label>{' '}
      <Button type="submit" size="sm" variant="secondary" loading={busy && !preview} disabled={!file || !passphrase}>
        Check the file
      </Button>
      {preview ? (
        <Alert variant={preview.warnings.length > 0 ? 'warning' : 'info'}>
          Exported by {preview.exportedBy} on {new Date(preview.exportedAtUtc).toLocaleString()}: {preview.users} users, {preview.hosts} hosts, {preview.credentials} credentials.
          {preview.warnings.map((w) => (
            <span key={w}>
              <br />
              {w}
            </span>
          ))}
          <br />
          <label>
            <input type="checkbox" checked={confirmed} onChange={(e) => setConfirmed(e.target.checked)} /> Replace this instance’s users and data with the file’s
          </label>{' '}
          <Button size="sm" variant="danger" loading={busy} disabled={!confirmed} onClick={() => void importNow()}>
            Import and sign out
          </Button>
        </Alert>
      ) : null}
    </form>
  )
}
