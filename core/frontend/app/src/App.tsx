import { useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, onUnauthorized, resetAntiforgeryToken } from '@nkeeper/sdk'
import { buildCatalog } from './catalog'
import { ChangePassword } from './ChangePassword'
import { DialogProvider } from './Dialogs'
import { loadPluginFrontends, type FrontendState, type ModuleImporter, type PluginStatus } from './plugins'
import { PluginsView } from './PluginsView'
import { UsersView } from './UsersView'
import { SignIn } from './SignIn'
import { ADMINISTRATOR, type CurrentUser } from './types'
import { Workspace } from './Workspace'

type Load =
  | { kind: 'loading' }
  | { kind: 'signedOut' }
  | { kind: 'mustChangePassword'; user: CurrentUser }
  | { kind: 'error'; message: string }
  | { kind: 'ready'; user: CurrentUser; plugins: PluginStatus[]; frontends: Map<string, FrontendState> }

/**
 * The application shell (docs/architecture.md §7.14): signs the user in, loads the plugins and their
 * frontends, then shows the workspace. A session that ends anywhere brings the sign-in form back.
 */
export function App({ importer }: { importer?: ModuleImporter }) {
  const [load, setLoad] = useState<Load>({ kind: 'loading' })
  const [attempt, setAttempt] = useState(0)
  const [view, setView] = useState<'workspace' | 'plugins' | 'users'>('workspace')
  const [signOutError, setSignOutError] = useState<string | null>(null)
  // Bumped whenever the session ends, so a load still in flight can't bring back the signed-in view.
  const session = useRef(0)

  useEffect(
    () =>
      onUnauthorized(() => {
        session.current += 1
        setLoad({ kind: 'signedOut' })
      }),
    [],
  )

  useEffect(() => {
    let cancelled = false
    const started = session.current
    const current = () => !cancelled && session.current === started
    void (async () => {
      try {
        const user = await api.get<CurrentUser>('/api/auth/me', { suppressUnauthorizedEvent: true })
        if (user.mustChangePassword) {
          if (current()) {
            setLoad({ kind: 'mustChangePassword', user })
          }
          return
        }
        const plugins = await api.get<PluginStatus[]>('/api/plugins')
        const frontends = await loadPluginFrontends(plugins, importer)
        if (current()) {
          setLoad({ kind: 'ready', user, plugins, frontends })
        }
      } catch (error) {
        if (current()) {
          setLoad(
            error instanceof ApiError && error.status === 401
              ? { kind: 'signedOut' }
              : { kind: 'error', message: error instanceof ApiError ? error.message : 'NKeeper could not be loaded.' },
          )
        }
      }
    })()
    return () => {
      cancelled = true
    }
  }, [importer, attempt])

  const catalog = useMemo(() => (load.kind === 'ready' ? buildCatalog(load.plugins, load.frontends) : null), [load])

  // Only a sign-out the server confirmed shows the sign-in form: until then the session may still be valid.
  async function signOut() {
    setSignOutError(null)
    try {
      await api.post('/api/auth/logout')
    } catch (error) {
      setSignOutError(error instanceof ApiError ? error.message : 'Signing out failed. Try again.')
      return
    }
    resetAntiforgeryToken()
    session.current += 1
    setView('workspace')
    setLoad({ kind: 'signedOut' })
  }

  const isAdministrator = load.kind === 'ready' && load.user.roles.includes(ADMINISTRATOR)
  return (
    <div className="nk-app">
      <header className="nk-header">
        <h1>NKeeper</h1>
        {(load.kind === 'ready' || load.kind === 'mustChangePassword') && (
          <div className="nk-header-tools">
            {isAdministrator && (
              <>
                <button type="button" aria-pressed={view === 'users'} onClick={() => setView((v) => (v === 'users' ? 'workspace' : 'users'))}>
                  Users
                </button>
                <button type="button" aria-pressed={view === 'plugins'} onClick={() => setView((v) => (v === 'plugins' ? 'workspace' : 'plugins'))}>
                  Plugins
                </button>
              </>
            )}
            <span className="nk-user">{load.user.userName}</span>
            <button type="button" onClick={() => void signOut()}>
              Sign out
            </button>
            {signOutError && (
              <span role="alert" className="nk-header-error">
                {signOutError}
              </span>
            )}
          </div>
        )}
      </header>
      {load.kind === 'signedOut' && (
        <main className="nk-center">
          <SignIn onSignedIn={() => setAttempt((n) => n + 1)} />
        </main>
      )}
      {load.kind === 'mustChangePassword' && (
        <main className="nk-center">
          <ChangePassword onChanged={() => setAttempt((n) => n + 1)} />
        </main>
      )}
      {load.kind === 'loading' && <p className="nk-muted nk-pad">Loading…</p>}
      {load.kind === 'error' && (
        <p className="nk-alert nk-pad" role="alert">
          {load.message}
        </p>
      )}
      {load.kind === 'ready' && catalog && (
        <DialogProvider>
          {/* The workspace stays mounted behind the plugin list, so its sessions and drafts carry on. */}
          {view === 'plugins' && isAdministrator && <PluginsView plugins={load.plugins} frontends={load.frontends} />}
          {view === 'users' && isAdministrator && (
            <div className="nk-view">
              <UsersView currentUserId={load.user.id} />
            </div>
          )}
          <div className="nk-view" hidden={view !== 'workspace' && isAdministrator}>
            <Workspace catalog={catalog} canCreate={isAdministrator} />
          </div>
        </DialogProvider>
      )}
    </div>
  )
}
