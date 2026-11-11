import type { ComponentType } from 'react'
import type { SessionViewProps } from '@nkeeper/sdk'
import { credentialName, serviceName, usedWith } from './hosts'
import type { FrontendState, PluginStatus } from './plugins'
import type { Host, HostActionInfo, ServiceTypeInfo, SessionTypeInfo } from './types'

/**
 * What the loaded plugins contribute, joined from `GET /api/plugins` and their frontends
 * (docs/architecture.md §7.12, §7.14). The shell knows no service type, session type or plugin itself.
 */
export interface Catalog {
  serviceTypes: Map<string, ServiceTypeInfo>
  sessionTypes: Map<string, SessionTypeInfo & { pluginId: string }>
  actions: (HostActionInfo & { pluginId: string })[]
  /** Session views by session type, each from the plugin that contributed the type. */
  views: Map<string, ComponentType<SessionViewProps>>
}

export function buildCatalog(statuses: PluginStatus[], frontends: Map<string, FrontendState>): Catalog {
  const catalog: Catalog = { serviceTypes: new Map(), sessionTypes: new Map(), actions: [], views: new Map() }
  for (const status of statuses) {
    if (status.state !== 'loaded') {
      continue
    }
    for (const type of status.serviceTypes ?? []) {
      catalog.serviceTypes.set(type.id, type)
    }
    for (const type of status.sessionTypes ?? []) {
      catalog.sessionTypes.set(type.id, { ...type, pluginId: status.id })
    }
    for (const action of status.hostActions ?? []) {
      catalog.actions.push({ ...action, pluginId: status.id })
    }
    const frontend = frontends.get(status.id)
    const views = frontend?.kind === 'active' ? frontend.definition.sessionViews : undefined
    for (const [sessionType, view] of Object.entries(views ?? {})) {
      // A plugin draws only its own session types.
      if (status.sessionTypes?.some((t) => t.id === sessionType)) {
        catalog.views.set(sessionType, view)
      }
    }
  }
  return catalog
}

/** How a session gets its credential. */
export type SessionCredential = { kind: 'saved'; id: string; name: string } | { kind: 'typed' } | { kind: 'none' }

/** What choosing an entry does: open a session, or run a plugin's endpoint (docs/architecture.md §7.12). */
export type EntryTarget = { kind: 'session'; sessionType: string } | { kind: 'run'; pluginId: string; endpoint: string }

export interface ConnectEntry {
  key: string
  label: string
  target: EntryTarget
  serviceId: string
  credential: SessionCredential
}

export interface ConnectGroup {
  key: string
  /** The action and the service, e.g. "Terminal · SSH :22". */
  label: string
  /** The action's own label, for the tab. */
  actionLabel: string
  entries: ConnectEntry[]
}

/** What an action does, and the service types it applies to; null when it can't be offered on this host. */
function resolve(action: Catalog['actions'][number], host: Host, catalog: Catalog): { target: EntryTarget; serviceTypes: string[] } | null {
  if (action.endpoint) {
    return { target: { kind: 'run', pluginId: action.pluginId, endpoint: action.endpoint }, serviceTypes: action.serviceTypes }
  }
  const sessionType = action.sessionType ? catalog.sessionTypes.get(action.sessionType) : undefined
  if (!sessionType || sessionType.pluginId !== action.pluginId || !catalog.views.has(sessionType.id) || (host.jump && !sessionType.supportsJumpHost)) {
    return null
  }
  return { target: { kind: 'session', sessionType: sessionType.id }, serviceTypes: sessionType.serviceTypes }
}

/**
 * A host's menu (docs/architecture.md §7.12): for each action, one group per service it applies to; with a
 * credential required, an entry per saved credential used with the service, and one to type a credential.
 * A session action whose session type has no view, or can't go through the host's jump host, offers nothing.
 * Run actions run a plugin's endpoint instead.
 */
export function connectMenu(host: Host, catalog: Catalog): ConnectGroup[] {
  const groups: ConnectGroup[] = []
  for (const action of catalog.actions) {
    const resolved = resolve(action, host, catalog)
    if (!resolved) {
      continue
    }
    host.services.forEach((service, index) => {
      if (!service.available || !resolved.serviceTypes.includes(service.type)) {
        return
      }
      const base = { target: resolved.target, serviceId: service.id }
      const entries: ConnectEntry[] =
        action.credential === 'none'
          ? [{ ...base, key: `${action.id}/${service.id}`, label: action.label, credential: { kind: 'none' } }]
          : [
              ...host.credentials
                .filter((c) => usedWith(c, index))
                .map((c): ConnectEntry => ({ ...base, key: `${action.id}/${service.id}/${c.id}`, label: credentialName(c), credential: { kind: 'saved', id: c.id, name: credentialName(c) } })),
              { ...base, key: `${action.id}/${service.id}/typed`, label: 'Enter credentials…', credential: { kind: 'typed' } },
            ]
      groups.push({ key: `${action.id}/${service.id}`, label: `${action.label} · ${serviceName(service, catalog.serviceTypes)}`, actionLabel: action.label, entries })
    })
  }
  return groups
}
