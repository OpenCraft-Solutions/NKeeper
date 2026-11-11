import { createElement, useCallback, useEffect, useMemo, useState } from 'react'
import { api, ApiError, openStreamSession, SessionError, type StreamStart } from '@nkeeper/sdk'
import type { Catalog, ConnectEntry, ConnectGroup, SessionCredential } from './catalog'
import { useDialogs } from './dialogContext'
import { HostDetails } from './HostDetails'
import { HostEditor } from './HostEditor'
import { HostTree } from './HostTree'
import { serviceName } from './hosts'
import type { Host, ServiceHealth } from './types'

/** How often the shell refreshes health results; core checks in the background on its own schedule. */
const HEALTH_REFRESH_MS = 60_000

interface SessionTab {
  id: string
  title: string
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
export function Workspace({ catalog, canCreate }: { catalog: Catalog; canCreate: boolean }) {
  const [hosts, setHosts] = useState<Host[] | null>(null)
  const [loadError, setLoadError] = useState<string | null>(null)
  const [health, setHealth] = useState(new Map<string, ServiceHealth>())
  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [pane, setPane] = useState<Pane>({ kind: 'details' })
  const [sessions, setSessions] = useState<SessionTab[]>([])
  const [activeTab, setActiveTab] = useState<string>('host')

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
  const replaceHost = (host: Host) => setHosts((current) => (current ?? []).map((h) => (h.id === host.id ? host : h)))

  function select(host: Host) {
    setSelectedId(host.id)
    setPane({ kind: 'details' })
    setActiveTab('host')
  }

  function open(host: Host, group: ConnectGroup, entry: ConnectEntry) {
    const service = host.services.find((s) => s.id === entry.serviceId)
    const tab: SessionTab = {
      id: `tab-${++tabs}`,
      title: `${host.name} · ${group.actionLabel}`,
      host: { id: host.id, name: host.name },
      serviceId: entry.serviceId,
      serviceName: service ? serviceName(service, catalog.serviceTypes) : 'the service',
      sessionType: entry.sessionType,
      credential: entry.credential,
    }
    setSessions((current) => [...current, tab])
    setActiveTab(tab.id)
  }

  function close(id: string) {
    setSessions((current) => current.filter((t) => t.id !== id))
    setActiveTab((current) => (current === id ? 'host' : current))
  }

  async function checkHost(hostId: string) {
    const results = await api.post<ServiceHealth[]>(`/api/hosts/${hostId}/health`)
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

  return (
    <div className="nk-workspace">
      <aside className="nk-sidebar">
        {canCreate && (
          <div className="nk-pad">
            <button
              type="button"
              className="nk-primary"
              onClick={() => {
                setPane({ kind: 'create' })
                setActiveTab('host')
              }}
            >
              New host
            </button>
          </div>
        )}
        {loadError && (
          <p className="nk-alert nk-pad" role="alert">
            {loadError}
          </p>
        )}
        {hosts === null && !loadError ? (
          <p className="nk-muted nk-pad">Loading hosts…</p>
        ) : (
          <HostTree hosts={hosts ?? []} health={health} catalog={catalog} selectedId={selectedId} onSelect={select} onConnect={open} />
        )}
      </aside>
      <section className="nk-main">
        <div className="nk-tabs" role="tablist" aria-label="Open tabs">
          <button type="button" role="tab" id="tab-host" aria-selected={activeTab === 'host'} aria-controls="panel-host" onClick={() => setActiveTab('host')}>
            {pane.kind === 'create' ? 'New host' : (selected?.name ?? 'Host')}
          </button>
          {sessions.map((tab) => (
            <span key={tab.id} className="nk-tab">
              <button type="button" role="tab" id={tab.id} aria-selected={activeTab === tab.id} aria-controls={`panel-${tab.id}`} onClick={() => setActiveTab(tab.id)}>
                {tab.title}
              </button>
              <button type="button" className="nk-tab-close" aria-label={`Close ${tab.title}`} onClick={() => close(tab.id)}>
                ×
              </button>
            </span>
          ))}
        </div>
        <div className="nk-panel" role="tabpanel" id="panel-host" aria-labelledby="tab-host" hidden={activeTab !== 'host'}>
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
              onConnect={(group, entry) => open(selected, group, entry)}
            />
          ) : (
            <p className="nk-muted nk-pad">Select a host to see its details, or connect to it from its menu.</p>
          )}
        </div>
        {sessions.map((tab) => (
          <div key={tab.id} className="nk-panel nk-session" role="tabpanel" id={`panel-${tab.id}`} aria-labelledby={tab.id} hidden={activeTab !== tab.id}>
            <SessionPanel tab={tab} catalog={catalog} visible={activeTab === tab.id} hostName={hostName} />
          </div>
        ))}
      </section>
    </div>
  )
}

/** One session's tab: the plugin's view, given a `connect` that runs core's control phase with core's dialogs. */
function SessionPanel({
  tab,
  catalog,
  visible,
  hostName,
}: {
  tab: SessionTab
  catalog: Catalog
  visible: boolean
  hostName: (id: string) => string | null
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

  if (!View) {
    return <p className="nk-alert nk-pad">The plugin that draws this session isn’t loaded any more.</p>
  }
  // The plugin's component, from the catalog: the same one each render.
  return createElement(View, { host, serviceId: tab.serviceId, connect, visible })
}
