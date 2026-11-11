import { createElement, useCallback, useEffect, useMemo, useRef, useState } from 'react'
import { api, ApiError, openStreamSession, SessionError, type StreamStart, type TrustPrompt } from '@nkeeper/sdk'
import { connectMenu, type Catalog, type ConnectEntry, type ConnectGroup, type SessionCredential } from './catalog'
import { useDialogs } from './dialogContext'
import { HostDetails } from './HostDetails'
import { HostEditor } from './HostEditor'
import { HostTree } from './HostTree'
import { groupHosts, matchesSearch, serviceName, tagNames } from './hosts'
import type { Host, ServiceHealth } from './types'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import { ContextMenu, type MenuEntry } from './ui/ContextMenu'
import picker from './ui/GroupByPicker.module.css'
import { Spinner } from './ui/Spinner'
import layout from './ui/Workspace.module.css'

const GROUP_BY_KEY = 'nkeeper.groupBy'

/** The grouping is kept in this browser only; storage that's blocked just means no memory of it. */
function readGroupBy(): string {
  try {
    return localStorage.getItem(GROUP_BY_KEY) ?? ''
  } catch {
    return ''
  }
}

function writeGroupBy(value: string) {
  try {
    localStorage.setItem(GROUP_BY_KEY, value)
  } catch {
    // Not kept.
  }
}

/** How often the shell refreshes health results; core checks in the background on its own schedule. */
const HEALTH_REFRESH_MS = 60_000

interface SessionTab {
  id: string
  /** The host's name, and under it what the tab shows: the action. */
  title: string
  detail: string
  host: { id: string; name: string }
  serviceId: string
  serviceName: string
  sessionType: string
  credential: SessionCredential
}

type Pane = { kind: 'details' } | { kind: 'edit'; hostId: string } | { kind: 'create' }

let tabs = 0

/**
 * The workspace (docs/architecture.md §7.14): the host tree on the left, tabs on the right. The first tab
 * is always the selected host; each session opens in a tab of its own, which stays mounted while hidden.
 */
export function Workspace({
  catalog,
  canCreate,
  focus = null,
}: {
  catalog: Catalog
  canCreate: boolean
  /** A host to show, from another page; each new `n` shows it again. */
  focus?: { hostId: string; n: number } | null
}) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [health, setHealth] = useState(new Map<string, ServiceHealth>())
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [pane, setPane] = useState<Pane>({ kind: 'details' })
  const [sessions, setSessions] = useState<SessionTab[]>([])
  const [activeTab, setActiveTab] = useState<string>('host')
  // What the last run action answered, shown until dismissed.
  const [notice, setNotice] = useState<{ ok: boolean; text: string } | null>(null)
  const [query, setQuery] = useState('')
  const [groupBy, setGroupBy] = useState(readGroupBy)
  const [collapsed, setCollapsed] = useState(new Set<string>())
  // The host menu (right-click) or the Connect menu (the details' button), at the pointer or the button.
  const [menu, setMenu] = useState<{ hostId: string; x: number; y: number; connectOnly: boolean } | null>(null)
  const dialogs = useDialogs()
  // The latest health check asked for, by host.
  const checks = useRef(new Map<string, number>())

  const [reloads, setReloads] = useState(0)
  const loadHosts = useCallback(() => setReloads((n) => n + 1), [])

  useEffect(() => {
    let cancelled = false
    api.get<Host[]>('/api/hosts').then(
      (loaded) => {
        if (!cancelled) {
          setHosts(loaded)
          setLoadError(null)
        }
      },
      (error: unknown) => {
        if (!cancelled) {
          setLoadError(error instanceof ApiError ? error.message : 'The hosts could not be loaded.')
        }
      },
    )
    return () => {
      cancelled = true
    }
  }, [reloads])

  useEffect(() => {
    let cancelled = false
    const refresh = async () => {
      try {
        const results = await api.get<ServiceHealth[]>('/api/health')
        if (!cancelled) {
          setHealth(new Map(results.map((r) => [r.serviceId, r])))
        }
      } catch {
        // Health is a nicety: the tree works without it.
      }
    }
    void refresh()
    const timer = setInterval(() => void refresh(), HEALTH_REFRESH_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  const selected = hosts?.find((h) => h.id === selectedId) ?? null
  // A jump host's key prompt names the jump host, which the user may not be able to see.
  const hostName = useCallback((id: string) => hosts?.find((h) => h.id === id)?.name ?? null, [hosts])
  const canManage = useCallback((id: string) => hosts?.find((h) => h.id === id)?.canManage ?? false, [hosts])
  const replaceHost = (host: Host) => setHosts((current) => (current ?? []).map((h) => (h.id === host.id ? host : h)))

  // A host shown from another page: selected once per request, while rendering (no effect needed).
  const [shownFocus, setShownFocus] = useState(focus)
  if (focus !== shownFocus) {
    setShownFocus(focus)
    if (focus) {
      setSelectedId(focus.hostId)
      setPane({ kind: 'details' })
      setActiveTab('host')
    }
  }

  function select(host: Host) {
    setSelectedId(host.id)
    setPane({ kind: 'details' })
    setActiveTab('host')
  }

  function open(host: Host, group: ConnectGroup, entry: ConnectEntry) {
    const service = host.services.find((s) => s.id === entry.serviceId)
    if (entry.target.kind === 'run') {
      void run(host, group, entry, entry.target, service ? serviceName(service, catalog.serviceTypes) : 'the service')
      return
    }
    const tab: SessionTab = {
      id: `tab-${++tabs}`,
      title: host.name,
      detail: group.actionLabel,
      host: { id: host.id, name: host.name },
      serviceId: entry.serviceId,
      serviceName: service ? serviceName(service, catalog.serviceTypes) : 'the service',
      sessionType: entry.target.sessionType,
      credential: entry.credential,
    }
    setSessions((current) => [...current, tab])
    setActiveTab(tab.id)
  }

  /** A run action (docs/architecture.md §7.12): the plugin's endpoint on the host, and the message it answers. */
  async function run(host: Host, group: ConnectGroup, entry: ConnectEntry, target: Extract<ConnectEntry['target'], { kind: 'run' }>, service: string) {
    const body: Record<string, unknown> = { serviceId: entry.serviceId }
    try {
      if (entry.credential.kind === 'saved') {
        body.credentialId = entry.credential.id
      } else if (entry.credential.kind === 'typed') {
        const typed = await dialogs.askCredential(host.name, service)
        if (!typed) {
          return
        }
        body.promptReference = (await api.post<{ reference: string }>(`/api/hosts/${host.id}/services/${entry.serviceId}/credential-prompt`, typed)).reference
      }
      const result = await api.post<{ message?: string } | undefined>(`/api/p/${encodeURIComponent(target.pluginId)}/hosts/${host.id}/${target.endpoint}`, body)
      setNotice({ ok: true, text: result?.message ?? `${group.actionLabel}: done.` })
    } catch (error) {
      setNotice({ ok: false, text: error instanceof ApiError ? error.message : `${group.actionLabel} failed.` })
    }
  }

  function close(id: string) {
    setSessions((current) => current.filter((t) => t.id !== id))
    setActiveTab((current) => (current === id ? 'host' : current))
  }

  async function checkHost(hostId: string) {
    // Only the latest check of a host counts: an older one that finishes later is ignored.
    const generation = (checks.current.get(hostId) ?? 0) + 1
    checks.current.set(hostId, generation)
    const results = await api.post<ServiceHealth[]>(`/api/hosts/${hostId}/health`)
    if (checks.current.get(hostId) !== generation) {
      return
    }
    setHealth((current) => {
      const next = new Map([...current].filter(([, r]) => r.hostId !== hostId))
      results.forEach((r) => next.set(r.serviceId, r))
      return next
    })
  }

  async function checkSelected() {
    if (selected) {
      await checkHost(selected.id)
    }
  }

  /** A host just created or saved is checked at once, rather than at the next background round. */
  function checkSaved(host: Host) {
    checkHost(host.id).catch(() => {
      // Health is a nicety: the host is saved either way.
    })
  }

  const names = useMemo(() => tagNames(hosts ?? []), [hosts])
  const effectiveGroupBy = names.includes(groupBy) ? groupBy : ''
  const visibleHosts = useMemo(() => [...(hosts ?? [])].filter((h) => matchesSearch(h, query)).sort((a, b) => a.name.localeCompare(b.name)), [hosts, query])
  const groups = useMemo(() => groupHosts(visibleHosts, effectiveGroupBy || null), [visibleHosts, effectiveGroupBy])
  const menuHost = menu ? (hosts?.find((h) => h.id === menu.hostId) ?? null) : null

  /** 1.x's Connect entries: one per action and service, with its credentials in a submenu. */
  function connectEntries(host: Host): MenuEntry[] {
    return connectMenu(host, catalog).map((group): MenuEntry => {
      const only = group.entries.length === 1 && group.entries[0]?.credential.kind === 'none' ? group.entries[0] : undefined
      return only
        ? { kind: 'item', label: group.label, onSelect: () => open(host, group, only) }
        : {
            kind: 'item',
            label: group.label,
            submenu: group.entries.map((entry): MenuEntry => ({ kind: 'item', label: entry.label, onSelect: () => open(host, group, entry) })),
          }
    })
  }

  /** 1.x's host menu: Connect, Edit, and Copy. */
  function hostEntries(host: Host): MenuEntry[] {
    const connect = connectEntries(host)
    const copy = (text: string) => void navigator.clipboard?.writeText(text).catch(() => {})
    const edit: MenuEntry[] = host.canManage
      ? [
          {
            kind: 'item',
            label: 'Edit',
            onSelect: () => {
              select(host)
              setPane({ kind: 'edit', hostId: host.id })
            },
          },
        ]
      : []
    return [
      { kind: 'header', label: host.name },
      { kind: 'item', label: 'Connect', disabled: connect.length === 0, submenu: connect.length > 0 ? connect : undefined },
      { kind: 'separator' },
      ...edit,
      {
        kind: 'item',
        label: 'Copy',
        submenu: [
          { kind: 'item', label: 'Hostname', detail: host.hostname ?? undefined, disabled: !host.hostname, onSelect: () => host.hostname && copy(host.hostname) },
          { kind: 'item', label: 'IP', detail: host.ipAddress ?? undefined, disabled: !host.ipAddress, onSelect: () => host.ipAddress && copy(host.ipAddress) },
        ],
      },
    ]
  }

  /** Double-click: the first session entry, with the first saved credential if there is one. Never a run action. */
  function activate(host: Host) {
    const first = connectMenu(host, catalog).find((g) => g.entries[0]?.target.kind === 'session')
    const entry = first?.entries[0]
    if (first && entry) {
      open(host, first, entry)
    }
  }

  const createHost = () => {
    setPane({ kind: 'create' })
    setActiveTab('host')
  }
  const allCollapsed = groups.length > 0 && collapsed.size >= groups.length

  return (
    <div className={layout.workspace}>
      <aside className={layout.sidebar} aria-label="Host inventory">
        <div className={layout.sidebarHeader}>
          <h1 className={layout.sidebarTitle}>
            Hosts
            {hosts && <span className={layout.total}>{visibleHosts.length === hosts.length ? hosts.length : `${visibleHosts.length} of ${hosts.length}`}</span>}
          </h1>
          <div className={layout.sidebarActions}>
            {effectiveGroupBy && (
              <button
                type="button"
                className={layout.iconButton}
                onClick={() => setCollapsed(allCollapsed ? new Set() : new Set(groups.map((g) => g.key)))}
                aria-label={allCollapsed ? 'Expand all groups' : 'Collapse all groups'}
                title={allCollapsed ? 'Expand all' : 'Collapse all'}
              >
                <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
                  <path
                    d={allCollapsed ? 'M4.5 3.5 8 7l3.5-3.5M4.5 9 8 12.5 11.5 9' : 'M4.5 7 8 3.5 11.5 7M4.5 12.5 8 9l3.5 3.5'}
                    fill="none"
                    stroke="currentColor"
                    strokeWidth="1.5"
                    strokeLinecap="round"
                    strokeLinejoin="round"
                  />
                </svg>
              </button>
            )}
            <button type="button" className={layout.iconButton} onClick={loadHosts} aria-label="Refresh hosts" title="Refresh">
              <svg viewBox="0 0 16 16" width="16" height="16" aria-hidden="true">
                <path d="M13.5 8a5.5 5.5 0 1 1-1.6-3.9M13.5 2.5v3h-3" fill="none" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" strokeLinejoin="round" />
              </svg>
            </button>
          </div>
        </div>

        {canCreate && (
          <div className={layout.addActions}>
            <Button size="sm" onClick={createHost}>
              New host
            </Button>
          </div>
        )}

        <div className={layout.controls}>
          <input
            className={layout.search}
            type="search"
            value={query}
            onChange={(event) => setQuery(event.target.value)}
            placeholder="Search, or env=prod"
            aria-label="Search hosts"
            spellCheck={false}
          />
          <div className={picker.picker}>
            <span className={picker.label} aria-hidden="true">
              Group by
            </span>
            <select
              className={picker.add}
              value={effectiveGroupBy}
              onChange={(event) => {
                setCollapsed(new Set())
                setGroupBy(event.target.value)
                writeGroupBy(event.target.value)
              }}
              aria-label="Group by"
            >
              <option value="">None</option>
              {names.map((name) => (
                <option key={name} value={name}>
                  Tag: {name}
                </option>
              ))}
            </select>
          </div>
        </div>

        <div className={layout.treeScroll}>
          {loadError && !hosts ? (
            <div className={layout.sidebarState}>
              <Alert>{loadError}</Alert>
              <Button variant="secondary" size="sm" onClick={loadHosts}>
                Try again
              </Button>
            </div>
          ) : !hosts ? (
            <div className={layout.sidebarState}>
              <Spinner label="Loading hosts" />
            </div>
          ) : hosts.length === 0 ? (
            <p className={layout.sidebarState}>{canCreate ? 'No hosts yet.' : 'No hosts have been shared with you yet.'}</p>
          ) : visibleHosts.length === 0 ? (
            <div className={layout.sidebarState}>
              <p>No hosts match “{query}”.</p>
              <Button variant="ghost" size="sm" onClick={() => setQuery('')}>
                Clear search
              </Button>
            </div>
          ) : (
            <HostTree
              groups={groups}
              grouped={!!effectiveGroupBy}
              collapsed={collapsed}
              onToggle={(key) =>
                setCollapsed((current) => {
                  const next = new Set(current)
                  if (!next.delete(key)) {
                    next.add(key)
                  }
                  return next
                })
              }
              health={health}
              catalog={catalog}
              selectedId={selectedId}
              menuHostId={menu?.hostId ?? null}
              onSelect={select}
              onActivate={activate}
              onContextMenu={(host, x, y) => {
                select(host)
                setMenu({ hostId: host.id, x, y, connectOnly: false })
              }}
            />
          )}
        </div>
      </aside>

      <main className={layout.main}>
        {notice && (
          <div className={layout.notice}>
            <Alert variant={notice.ok ? 'info' : 'error'}>{notice.text}</Alert>
            <Button variant="ghost" size="sm" onClick={() => setNotice(null)}>
              Dismiss
            </Button>
          </div>
        )}

        {sessions.length > 0 && (
          <div className={layout.tabs} role="tablist" aria-label="Open views">
            <button type="button" role="tab" id="tab-host" className={layout.tab} aria-selected={activeTab === 'host'} aria-controls="panel-host" onClick={() => setActiveTab('host')}>
              <span className={layout.tabTitle}>{pane.kind === 'create' ? 'New host' : (selected?.name ?? 'Details')}</span>
              <span className={layout.tabDetail}>Details</span>
            </button>
            {sessions.map((tab) => (
              <div key={tab.id} className={`${layout.tabWrap} ${activeTab === tab.id ? layout.tabWrapActive : ''}`}>
                <button
                  type="button"
                  role="tab"
                  id={tab.id}
                  className={layout.tab}
                  aria-selected={activeTab === tab.id}
                  aria-controls={`panel-${tab.id}`}
                  onClick={() => setActiveTab(tab.id)}
                  onAuxClick={(event) => event.button === 1 && close(tab.id)}
                  aria-label={`${tab.title} · ${tab.detail}`}
                  title={`${tab.title} — ${tab.detail}`}
                >
                  <span className={layout.tabTitle}>{tab.title}</span>
                  <span className={layout.tabDetail}>{tab.detail}</span>
                </button>
                <button type="button" className={layout.tabClose} onClick={() => close(tab.id)} aria-label={`Close ${tab.title} · ${tab.detail}`} title="Close">
                  ×
                </button>
              </div>
            ))}
          </div>
        )}

        <div className={layout.view} role="tabpanel" id="panel-host" aria-labelledby="tab-host" hidden={activeTab !== 'host'}>
          {pane.kind === 'create' ? (
            <HostEditor
              host={null}
              hosts={hosts ?? []}
              serviceTypes={catalog.serviceTypes}
              onSaved={(host) => {
                setHosts((current) => [...(current ?? []), host].sort((a, b) => a.name.localeCompare(b.name)))
                setSelectedId(host.id)
                setPane({ kind: 'details' })
                checkSaved(host)
              }}
              onCancel={() => setPane({ kind: 'details' })}
              onReload={loadHosts}
            />
          ) : pane.kind === 'edit' && selected ? (
            <HostEditor
              key={`${selected.id}/${selected.version}`}
              host={selected}
              hosts={hosts ?? []}
              serviceTypes={catalog.serviceTypes}
              onSaved={(host) => {
                replaceHost(host)
                setPane({ kind: 'details' })
                checkSaved(host)
              }}
              onCancel={() => setPane({ kind: 'details' })}
              onReload={loadHosts}
            />
          ) : selected ? (
            <HostDetails
              key={selected.id}
              host={selected}
              hosts={hosts ?? []}
              catalog={catalog}
              health={health}
              onEdit={() => setPane({ kind: 'edit', hostId: selected.id })}
              onDeleted={() => {
                setHosts((current) => (current ?? []).filter((h) => h.id !== selected.id))
                setSelectedId(null)
              }}
              onKeyRemoved={(keyId) =>
                setHosts((current) => (current ?? []).map((h) => (h.id === selected.id ? { ...h, trustedKeys: h.trustedKeys.filter((k) => k.id !== keyId) } : h)))
              }
              onCheck={checkSelected}
              connectable={connectMenu(selected, catalog).length > 0}
              onConnectMenu={(anchor) => setMenu({ hostId: selected.id, x: anchor.left, y: anchor.bottom + 4, connectOnly: true })}
            />
          ) : (
            <div className={layout.mainState}>
              {hosts?.length === 0 && canCreate ? (
                <>
                  <p className={layout.stateTitle}>Your inventory is empty</p>
                  <p>Add your first host, with its services, credentials and tags.</p>
                  <Button onClick={createHost}>New host</Button>
                </>
              ) : hosts?.length === 0 ? (
                <>
                  <p className={layout.stateTitle}>No hosts yet</p>
                  <p>No hosts have been shared with you. Ask an administrator to give you access to the hosts or groups you need.</p>
                </>
              ) : (
                <>
                  <p className={layout.stateTitle}>Select a host</p>
                  <p>
                    Search by name, address or tag (<code>env=prod</code>), and group the tree by any tag.
                  </p>
                </>
              )}
            </div>
          )}
        </div>

        {sessions.map((tab) => (
          <div key={tab.id} className={layout.view} role="tabpanel" id={`panel-${tab.id}`} aria-labelledby={tab.id} hidden={activeTab !== tab.id}>
            <SessionPanel tab={tab} catalog={catalog} visible={activeTab === tab.id} hostName={hostName} canManage={canManage} />
          </div>
        ))}
      </main>

      {menu && menuHost && (
        <ContextMenu
          label={`Actions for ${menuHost.name}`}
          entries={menu.connectOnly ? connectEntries(menuHost) : hostEntries(menuHost)}
          x={menu.x}
          y={menu.y}
          onClose={() => setMenu(null)}
        />
      )}
    </div>
  )
}

/** One session's tab: the plugin's view, given a `connect` that runs core's control phase with core's dialogs. */
function SessionPanel({
  tab,
  catalog,
  visible,
  hostName,
  canManage,
}: {
  tab: SessionTab
  catalog: Catalog
  visible: boolean
  hostName: (id: string) => string | null
  canManage: (id: string) => boolean
}) {
  const dialogs = useDialogs()
  const View = catalog.views.get(tab.sessionType)
  const host = useMemo(() => tab.host, [tab.host])

  const connect = useCallback(
    async (options?: Record<string, unknown>, signal?: AbortSignal) => {
      const start: { -readonly [K in keyof StreamStart]: StreamStart[K] } = { hostId: tab.host.id, serviceId: tab.serviceId, options }
      if (tab.credential.kind === 'saved') {
        start.credentialId = tab.credential.id
      } else if (tab.credential.kind === 'typed') {
        const typed = await dialogs.askCredential(tab.host.name, tab.serviceName, signal)
        if (signal?.aborted) {
          throw new SessionError('aborted', 'The session was cancelled.')
        }
        if (!typed) {
          throw new SessionError('cancelled', 'No credential was given, so the session didn’t open.')
        }
        try {
          const prompt = await api.post<{ reference: string }>(`/api/hosts/${tab.host.id}/services/${tab.serviceId}/credential-prompt`, typed)
          start.promptReference = prompt.reference
        } catch (error) {
          throw new SessionError('credential_denied', error instanceof ApiError ? error.message : 'The credential could not be used.')
        }
      }
      return openStreamSession(tab.sessionType, start, {
        signal,
        confirmHostKey: (prompt) =>
          dialogs.confirmHostKey(
            prompt,
            prompt.hostId === tab.host.id ? tab.host.name : (hostName(prompt.hostId) ?? `the jump host of ${tab.host.name}`),
            signal,
          ),
      })
    },
    [dialogs, tab, hostName],
  )

  // For views that open sessions through their plugin's endpoints: core's dialog, then core's trust endpoint.
  // Only someone who can manage the host may trust for it (docs/architecture.md §7.7), so no one else is asked.
  const confirmTrust = useCallback(
    async (prompt: TrustPrompt) => {
      if (!canManage(prompt.hostId)) {
        return false
      }
      const name = prompt.hostId === tab.host.id ? tab.host.name : (hostName(prompt.hostId) ?? `the jump host of ${tab.host.name}`)
      if (!(await dialogs.confirmHostKey(prompt, name))) {
        return false
      }
      try {
        await api.post(`/api/hosts/${prompt.hostId}/services/${prompt.serviceId}/trusted-keys`, { algorithm: prompt.algorithm, fingerprint: prompt.fingerprint })
        return true
      } catch {
        return false
      }
    },
    [dialogs, tab, hostName, canManage],
  )

  if (!View) {
    return <p className="nk-alert nk-pad">The plugin that draws this session isn’t loaded any more.</p>
  }
  // The plugin's component, from the catalog: the same one each render.
  return createElement(View, { host, serviceId: tab.serviceId, connect, visible, confirmTrust })
}
