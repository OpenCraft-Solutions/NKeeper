import { useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { api, ApiError, onUnauthorized, resetAntiforgeryToken } from '@nkeeper/sdk'
import { ColorSchemeDialog } from './appearance/ColorSchemeDialog'
import { applyColorScheme, tokensFor, type ColorSchemePreferences } from './appearance/colorSchemes'
import { buildCatalog } from './catalog'
import { ChangePassword } from './ChangePassword'
import { DialogProvider } from './Dialogs'
import { loadPluginFrontends, type FrontendState, type ModuleImporter, type PluginStatus } from './plugins'
import { HealthView } from './HealthView'
import { PluginsView } from './PluginsView'
import { UsersView } from './UsersView'
import { SignIn } from './SignIn'
import { ADMINISTRATOR, type CurrentUser } from './types'
import { Workspace } from './Workspace'
import { Alert } from './ui/Alert'
import frame from './ui/AppLayout.module.css'
import { AuthLayout } from './ui/AuthLayout'
import { ContextMenu } from './ui/ContextMenu'
import { Logo } from './ui/Logo'
import reports from './ui/Reports.module.css'
import { Spinner } from './ui/Spinner'

type View = 'workspace' | 'reports' | 'users' | 'settings'

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
  const [view, setView] = useState<View>('workspace')
  const [menu, setMenu] = useState<{ x: number; y: number } | null>(null)
  const [choosingColors, setChoosingColors] = useState(false)
  // A host to show in the workspace, from the Health page.
  const [focus, setFocus] = useState<{ hostId: string; n: number } | null>(null)
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

  // The signed-in user's colour scheme, as soon as the shell knows who they are (§7.14).
  const colorSchemes = load.kind === 'ready' || load.kind === 'mustChangePassword' ? load.user.colorSchemes : null
  useEffect(() => {
    if (colorSchemes) {
      applyColorScheme(tokensFor(colorSchemes))
    }
  }, [colorSchemes])

  /** A scheme chosen in the dialog: shown at once; the dialog saves it. */
  function colorSchemesChanged(next: ColorSchemePreferences) {
    setLoad((current) => (current.kind === 'ready' ? { ...current, user: { ...current.user, colorSchemes: next } } : current))
  }

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
  if (load.kind === 'signedOut' || load.kind === 'mustChangePassword') {
    return (
      <AuthLayout title={load.kind === 'signedOut' ? 'Sign in to NKeeper' : 'Choose a new password'}>
        {load.kind === 'signedOut' ? <SignIn onSignedIn={() => setAttempt((n) => n + 1)} /> : <ChangePassword onChanged={() => setAttempt((n) => n + 1)} />}
      </AuthLayout>
    )
  }
  if (load.kind !== 'ready' || !catalog) {
    return (
      <div className={frame.status}>
        {load.kind === 'error' ? (
          <Alert>{load.message}</Alert>
        ) : (
          <Spinner label="Loading NKeeper" />
        )}
      </div>
    )
  }

  const tab = (id: View, label: string) => (
    <button type="button" className={`${frame.navLink} ${view === id ? frame.navLinkActive : ''}`} aria-current={view === id ? 'page' : undefined} onClick={() => setView(id)}>
      {label}
    </button>
  )
  return (
    <div className={frame.shell}>
      <header className={frame.header}>
        <Logo size={24} />
        <nav className={frame.nav} aria-label="Main">
          {tab('workspace', 'Hosts')}
          {tab('reports', 'Reports')}
          {isAdministrator && tab('users', 'Users')}
          {isAdministrator && tab('settings', 'Settings')}
        </nav>
        <button
          type="button"
          className={frame.accountButton}
          aria-haspopup="menu"
          aria-expanded={menu !== null}
          title={load.user.email ?? undefined}
          onClick={(event) => {
            const rect = event.currentTarget.getBoundingClientRect()
            setMenu({ x: rect.right - 200, y: rect.bottom + 4 })
          }}
        >
          <span className={frame.avatar} aria-hidden="true">
            {load.user.userName.slice(0, 1).toUpperCase()}
          </span>
          <span className={frame.user}>{load.user.userName}</span>
          <svg viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
            <path d="M2.5 4.5 6 8l3.5-3.5" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
        </button>
      </header>
      {signOutError && (
        <div className={frame.banner}>
          <Alert>{signOutError}</Alert>
        </div>
      )}
      <main className={frame.content}>
        <DialogProvider>
          {view === 'reports' && (
            <ReportsPage>
              <HealthView
                catalog={catalog}
                onShowHost={(hostId) => {
                  setFocus((current) => ({ hostId, n: (current?.n ?? 0) + 1 }))
                  setView('workspace')
                }}
              />
            </ReportsPage>
          )}
          {view === 'users' && isAdministrator && <UsersView currentUserId={load.user.id} />}
          {view === 'settings' && isAdministrator && <PluginsView plugins={load.plugins} frontends={load.frontends} />}
          {/* The workspace stays mounted behind the other pages, so its sessions and drafts carry on. */}
          <div className={frame.content} hidden={view !== 'workspace'}>
            <Workspace catalog={catalog} canCreate={isAdministrator} focus={focus} />
          </div>
        </DialogProvider>
      </main>
      {choosingColors && <ColorSchemeDialog preferences={load.user.colorSchemes} onChange={colorSchemesChanged} onClose={() => setChoosingColors(false)} />}
      {menu && (
        <ContextMenu
          label="Account"
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
          entries={[
            { kind: 'header', label: load.user.email ?? load.user.userName },
            { kind: 'item', label: 'Color scheme', onSelect: () => setChoosingColors(true) },
            { kind: 'separator' },
            { kind: 'item', label: 'Sign out', onSelect: () => void signOut() },
          ]}
        />
      )}
    </div>
  )
}

/** 1.x's Reports page: the list of reports on the left, the chosen one on the right. Health is the only one so far. */
function ReportsPage({ children }: { children: ReactNode }) {
  return (
    <div className={reports.page}>
      <nav className={reports.sidebar} aria-label="Reports">
        <h1 className={reports.title}>Reports</h1>
        <ul className={reports.reportList}>
          <li>
            <button type="button" className={`${reports.reportLink} ${reports.reportLinkActive}`} aria-current="page">
              <span>Health</span>
              <span className={reports.reportDescription}>What’s down, and certificates about to expire</span>
            </button>
          </li>
        </ul>
      </nav>
      <section className={reports.report}>{children}</section>
    </div>
  )
}
