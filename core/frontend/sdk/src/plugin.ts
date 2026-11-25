import type { ComponentType } from 'react'
import type { StreamConnection, TrustPrompt } from './sessions'

/** The SDK version this build of the shell implements; plugin manifests declare a compatible range. */
export const SDK_VERSION = '1.15.0'

/** What a plugin's frontend knows about itself. Its identity comes from the server's verified manifest. */
export interface PluginContext {
  readonly id: string
  readonly version: string
  /**
   * What a plugin this one depends on shares (its `exports`, SDK 1.8). Throws for a plugin that isn't in this
   * plugin's `dependsOn`, or whose frontend isn't active.
   */
  readonly use: (pluginId: string) => Readonly<Record<string, unknown>>
}

/** What a session view is given (docs/architecture.md §7.14, SDK 0.1.6). */
export interface SessionViewProps {
  /** The host the session is on, as its tab describes it. */
  readonly host: { readonly id: string; readonly name: string }
  /** The service it opens on. */
  readonly serviceId: string
  /**
   * Opens the stream: core's control phase, with the shell's dialogs for a server key and a typed
   * credential. Resolves once core says `connected`; rejects with a `SessionError`. Call it again to reconnect.
   * Aborting `signal`, when the view goes, cancels it and any dialog it is waiting on.
   */
  readonly connect: (options?: Record<string, unknown>, signal?: AbortSignal) => Promise<StreamConnection>
  /** Whether the view's tab is showing. A hidden view stays mounted, so its session keeps running. */
  readonly visible: boolean
  /**
   * For a view that opens its session through the plugin's own endpoints (SDK 0.1.8): shows core's dialog for a
   * key or certificate the endpoint reported, and when the user trusts it, posts it to core. Resolves to whether
   * it's now trusted.
   */
  readonly confirmTrust: (prompt: TrustPrompt) => Promise<boolean>
}

/** What a host panel is given (docs/architecture.md §7.39, SDK 1.13). */
export interface HostPanelProps {
  /** The host whose details are showing. */
  readonly host: { readonly id: string; readonly name: string; readonly hostname: string | null; readonly ipAddress: string | null }
  /** Whether the signed-in user may manage the host (edit it, trust for it), not only use it. */
  readonly canManage: boolean
}

/** One of the plugin's integrations, as its settings panel sees it (SDK 1.13). */
export interface IntegrationState {
  readonly name: string
  readonly label: string
  /** Whether an administrator has bound it; the shell draws the binding itself, above the panel. */
  readonly bound: boolean
}

/** What a settings panel is given (docs/architecture.md §7.39, SDK 1.13). */
export interface SettingsPanelProps {
  /** The plugin's integrations; `version` changes whenever a binding does, so the panel can test it again. */
  readonly integrations: readonly IntegrationState[]
  readonly version: number
  /**
   * Shows core's dialog for a certificate or key the plugin's endpoint reported (its `presented`), such as a bound
   * server's self-signed certificate, and posts it to core when the administrator trusts it. Resolves to whether it's
   * now trusted.
   */
  readonly confirmTrust: (prompt: TrustPrompt) => Promise<boolean>
}

/** What a report's view is given (docs/architecture.md §7.41, SDK 1.14). */
export interface ReportViewProps {
  /** Shows a host in the workspace, as the Health report's links do. */
  readonly showHost: (hostId: string) => void
}

/** A report on the Reports page (SDK 1.14). */
export interface PluginReport {
  readonly label: string
  readonly description: string
  readonly view: ComponentType<ReportViewProps>
  /** Lists it only for administrators. Its endpoints check that too: this only hides it. */
  readonly administrators?: boolean
}

/** What an inventory tool's view is given (SDK 1.14). */
export interface InventoryToolProps {
  /** Closes the tool's dialog. */
  readonly close: () => void
  /** Reloads the host tree, after the tool added or changed hosts. */
  readonly hostsChanged: () => void
}

/** A button above the host tree, for administrators, that opens the plugin's view in core's dialog (SDK 1.14). */
export interface InventoryTool {
  readonly label: string
  /** The button's tooltip. */
  readonly title?: string
  readonly view: ComponentType<InventoryToolProps>
}

/**
 * A plugin frontend (SDK v1, docs/plugin-authoring.md). The module named by the manifest's
 * `frontend.entry` default-exports one, made with {@link definePlugin}. More members arrive with
 * their first users; until SDK v1 this contract may change.
 */
export interface PluginDefinition {
  /** Must equal the plugin ID in the verified manifest; the shell refuses a mismatch. */
  readonly id: string
  /** Called once, after the shell has loaded the module. */
  activate?(context: PluginContext): void | Promise<void>
  /** The tabs of the plugin's session types, by session type ID (SDK 0.1.6). */
  readonly sessionViews?: Readonly<Record<string, ComponentType<SessionViewProps>>>
  /** What the plugins that depend on this one may use, through `context.use` (SDK 1.8). */
  readonly exports?: Readonly<Record<string, unknown>>
  /**
   * Panels in a host's details, by their titles (SDK 1.13). The shell shows them for every host; a panel draws
   * nothing (returns null) for a host it has nothing to say about.
   */
  readonly hostPanels?: Readonly<Record<string, ComponentType<HostPanelProps>>>
  /** A section of the Settings page, for administrators, below the plugin's integration bindings (SDK 1.13). */
  readonly settingsPanel?: ComponentType<SettingsPanelProps>
  /** Reports on the Reports page, by ID (SDK 1.14). */
  readonly reports?: Readonly<Record<string, PluginReport>>
  /** Administrators' tools above the host tree, by ID (SDK 1.14). */
  readonly inventoryTools?: Readonly<Record<string, InventoryTool>>
}

const ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*$/

/** Declares a plugin frontend. Validates the definition and freezes it. */
export function definePlugin<T extends PluginDefinition>(definition: T): Readonly<T> {
  if (!isPluginDefinition(definition)) {
    throw new TypeError(
      'definePlugin needs an object with a valid plugin id; activate (if any) must be a function, sessionViews and hostPanels (if any) objects of components, settingsPanel (if any) a component, reports and inventoryTools (if any) objects of { label, view }, and exports (if any) an object.',
    )
  }
  return Object.freeze({
    ...definition,
    ...(definition.sessionViews ? { sessionViews: Object.freeze({ ...definition.sessionViews }) } : {}),
    ...(definition.exports ? { exports: Object.freeze({ ...definition.exports }) } : {}),
    ...(definition.hostPanels ? { hostPanels: Object.freeze({ ...definition.hostPanels }) } : {}),
    ...(definition.reports ? { reports: Object.freeze({ ...definition.reports }) } : {}),
    ...(definition.inventoryTools ? { inventoryTools: Object.freeze({ ...definition.inventoryTools }) } : {}),
  })
}

/** Whether a value has the shape of a plugin definition, e.g. a module's default export. */
export function isPluginDefinition(value: unknown): value is PluginDefinition {
  if (typeof value !== 'object' || value === null) {
    return false
  }
  const candidate = value as {
    id?: unknown
    activate?: unknown
    sessionViews?: unknown
    exports?: unknown
    hostPanels?: unknown
    settingsPanel?: unknown
    reports?: unknown
    inventoryTools?: unknown
  }
  return (
    typeof candidate.id === 'string' &&
    candidate.id.length <= 100 &&
    ID_PATTERN.test(candidate.id) &&
    (candidate.activate === undefined || typeof candidate.activate === 'function') &&
    (candidate.sessionViews === undefined || isViewMap(candidate.sessionViews)) &&
    (candidate.hostPanels === undefined || (isViewMap(candidate.hostPanels) && Object.keys(candidate.hostPanels as object).every(isPanelTitle))) &&
    (candidate.settingsPanel === undefined || isComponent(candidate.settingsPanel)) &&
    (candidate.reports === undefined || isEntryMap(candidate.reports)) &&
    (candidate.inventoryTools === undefined || isEntryMap(candidate.inventoryTools)) &&
    (candidate.exports === undefined || (typeof candidate.exports === 'object' && candidate.exports !== null && !Array.isArray(candidate.exports)))
  )
}

/** React's own component objects (memo, forwardRef, lazy) carry one of these. */
const COMPONENT_OBJECTS = new Set([Symbol.for('react.memo'), Symbol.for('react.forward_ref'), Symbol.for('react.lazy')])

/** Components are functions, or React's memo, forwardRef and lazy objects; anything else can't be drawn. */
function isComponent(view: unknown): boolean {
  if (typeof view === 'function') {
    return true
  }
  return typeof view === 'object' && view !== null && COMPONENT_OBJECTS.has((view as { $$typeof?: unknown }).$$typeof as symbol)
}

function isViewMap(value: unknown): boolean {
  return typeof value === 'object' && value !== null && !Array.isArray(value) && Object.values(value).every(isComponent)
}

function isPanelTitle(title: string): boolean {
  return title.trim().length > 0 && title.length <= 100
}

/** Reports and inventory tools: by ID, each with a label and a view, and any text fields as text. */
function isEntryMap(value: unknown): boolean {
  if (typeof value !== 'object' || value === null || Array.isArray(value)) {
    return false
  }
  return Object.entries(value).every(([id, entry]) => {
    if (!ID_PATTERN.test(id) || typeof entry !== 'object' || entry === null) {
      return false
    }
    const { label, view, description, title, administrators } = entry as Record<string, unknown>
    return (
      typeof label === 'string' &&
      isPanelTitle(label) &&
      isComponent(view) &&
      (description === undefined || typeof description === 'string') &&
      (title === undefined || typeof title === 'string') &&
      (administrators === undefined || typeof administrators === 'boolean')
    )
  })
}
