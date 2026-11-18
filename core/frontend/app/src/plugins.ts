import { isPluginDefinition, type PluginDefinition } from '@nkeeper/sdk'
import type { HostActionInfo, ServiceTypeInfo, SessionTypeInfo } from './types'

/** One plugin, as `GET /api/plugins` reports it. */
export interface PluginStatus {
  id: string
  version: string | null
  state: 'loaded' | 'disabled' | 'awaitingSignerApproval' | 'refused' | 'missing'
  reasonCode: string | null
  builtIn: boolean
  packageSha256: string | null
  keyId: string | null
  /** Same-origin URL of the plugin's frontend entry module, for loaded plugins that have one. */
  frontendEntry: string | null
  frontendEntrySha256: string | null
  /** For a loaded plugin, what it contributed (docs/architecture.md §7.4, §7.10, §7.12). */
  serviceTypes?: ServiceTypeInfo[] | null
  sessionTypes?: SessionTypeInfo[] | null
  hostActions?: HostActionInfo[] | null
  /** For a loaded plugin, the plugins it depends on (SDK 1.8). */
  dependsOn?: string[] | null
  /** Why the plugin needs a credential's plaintext, when its signed manifest says it does (SDK 1.10). */
  credentialRelease?: string | null
}

/** What happened to one plugin's frontend in this page. */
export type FrontendState =
  | { kind: 'none' }
  | { kind: 'active'; definition: PluginDefinition }
  | { kind: 'failed'; reason: string }

export type ModuleImporter = (url: string) => Promise<unknown>

/** The browser's own dynamic import; only ever given same-origin URLs from the server. */
export const browserImport: ModuleImporter = (url) => import(/* @vite-ignore */ url)

/**
 * Imports and activates each loaded plugin's frontend, a plugin's dependencies before it (SDK 1.8). A module
 * must default-export a plugin definition (definePlugin) whose ID matches the verified manifest; a failure
 * affects only that plugin, and the plugins that `use` it.
 */
export async function loadPluginFrontends(statuses: PluginStatus[], importer: ModuleImporter = browserImport): Promise<Map<string, FrontendState>> {
  const states = new Map<string, FrontendState>()
  for (const status of dependencyOrder(statuses)) {
    if (status.state !== 'loaded' || !status.frontendEntry) {
      states.set(status.id, { kind: 'none' })
      continue
    }
    if (!isSameOriginPluginUrl(status.frontendEntry, status.id)) {
      states.set(status.id, { kind: 'failed', reason: 'The entry URL is not one of this server’s plugin URLs.' })
      continue
    }
    try {
      const module = (await importer(status.frontendEntry)) as { default?: unknown }
      const definition = module.default
      if (!isPluginDefinition(definition)) {
        throw new Error('The module does not default-export a plugin definition (definePlugin).')
      }
      if (definition.id !== status.id) {
        throw new Error(`The module declares plugin '${definition.id}', but the server loaded '${status.id}'.`)
      }
      const dependsOn = status.dependsOn ?? []
      const use = (pluginId: string) => {
        if (!dependsOn.includes(pluginId)) {
          throw new Error(`'${status.id}' doesn't depend on '${pluginId}'.`)
        }
        const dependency = states.get(pluginId)
        if (dependency?.kind !== 'active') {
          throw new Error(`'${pluginId}', which '${status.id}' depends on, has no active frontend.`)
        }
        return dependency.definition.exports ?? Object.freeze({})
      }
      await definition.activate?.({ id: status.id, version: status.version ?? '', use })
      states.set(status.id, { kind: 'active', definition })
    } catch (error) {
      states.set(status.id, { kind: 'failed', reason: error instanceof Error ? error.message : String(error) })
    }
  }
  return states
}

/** The statuses with each plugin after the plugins it depends on; otherwise in the server's order. A cycle keeps the server's order. */
function dependencyOrder(statuses: PluginStatus[]): PluginStatus[] {
  const byId = new Map(statuses.map((s) => [s.id, s]))
  const ordered: PluginStatus[] = []
  const placed = new Set<string>()
  const visiting = new Set<string>()
  const place = (status: PluginStatus) => {
    if (placed.has(status.id) || visiting.has(status.id)) {
      return
    }
    visiting.add(status.id)
    for (const id of status.dependsOn ?? []) {
      const dependency = byId.get(id)
      if (dependency) {
        place(dependency)
      }
    }
    visiting.delete(status.id)
    placed.add(status.id)
    ordered.push(status)
  }
  statuses.forEach(place)
  return ordered
}

/** Only `/plugins/<that plugin's id>/…` on this origin is ever imported. */
function isSameOriginPluginUrl(url: string, id: string): boolean {
  try {
    const parsed = new URL(url, window.location.origin)
    return parsed.origin === window.location.origin && parsed.pathname.startsWith(`/plugins/${id}/`)
  } catch {
    return false
  }
}
