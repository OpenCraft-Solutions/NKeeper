import type { ComponentType } from 'react'
import type { StreamConnection } from './sessions'

/** The SDK version this build of the shell implements; plugin manifests declare a compatible range. */
export const SDK_VERSION = '0.1.6'

/** What a plugin's frontend knows about itself. Its identity comes from the server's verified manifest. */
export interface PluginContext {
  readonly id: string
  readonly version: string
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
   */
  readonly connect: (options?: Record<string, unknown>) => Promise<StreamConnection>
  /** Whether the view's tab is showing. A hidden view stays mounted, so its session keeps running. */
  readonly visible: boolean
}

/**
 * A plugin frontend (SDK v0, docs/v2-plan.md §4.1). The module named by the manifest's
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
}

const ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*$/

/** Declares a plugin frontend. Validates the definition and freezes it. */
export function definePlugin<T extends PluginDefinition>(definition: T): Readonly<T> {
  if (!isPluginDefinition(definition)) {
    throw new TypeError(
      'definePlugin needs an object with a valid plugin id; activate (if any) must be a function, and sessionViews (if any) an object of components.',
    )
  }
  return Object.freeze({ ...definition, ...(definition.sessionViews ? { sessionViews: Object.freeze({ ...definition.sessionViews }) } : {}) })
}

/** Whether a value has the shape of a plugin definition, e.g. a module's default export. */
export function isPluginDefinition(value: unknown): value is PluginDefinition {
  if (typeof value !== 'object' || value === null) {
    return false
  }
  const candidate = value as { id?: unknown; activate?: unknown; sessionViews?: unknown }
  return (
    typeof candidate.id === 'string' &&
    candidate.id.length <= 100 &&
    ID_PATTERN.test(candidate.id) &&
    (candidate.activate === undefined || typeof candidate.activate === 'function') &&
    (candidate.sessionViews === undefined || isViewMap(candidate.sessionViews))
  )
}

/** Components are functions, or objects for memo and forwardRef ones. */
function isViewMap(value: unknown): boolean {
  return (
    typeof value === 'object' &&
    value !== null &&
    !Array.isArray(value) &&
    Object.values(value).every((view) => typeof view === 'function' || (typeof view === 'object' && view !== null))
  )
}
