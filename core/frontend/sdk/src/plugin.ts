/** The SDK version this build of the shell implements; plugin manifests declare a compatible range. */
export const SDK_VERSION = '0.1.5'

/** What a plugin's frontend knows about itself. Its identity comes from the server's verified manifest. */
export interface PluginContext {
  readonly id: string
  readonly version: string
}

/**
 * A plugin frontend (SDK v0, docs/v2-plan.md §4.1). The module named by the manifest's
 * `frontend.entry` default-exports one, made with {@link definePlugin}. More members arrive with
 * their first users (tabs, menu actions, panels); until SDK v1 this contract may change.
 */
export interface PluginDefinition {
  /** Must equal the plugin ID in the verified manifest; the shell refuses a mismatch. */
  readonly id: string
  /** Called once, after the shell has loaded the module. */
  activate?(context: PluginContext): void | Promise<void>
}

const ID_PATTERN = /^[a-z0-9]+(?:-[a-z0-9]+)*(?:\.[a-z0-9]+(?:-[a-z0-9]+)*)*$/

/** Declares a plugin frontend. Validates the definition and freezes it. */
export function definePlugin<T extends PluginDefinition>(definition: T): Readonly<T> {
  if (!isPluginDefinition(definition)) {
    throw new TypeError('definePlugin needs an object with a valid plugin id, and activate (if any) must be a function.')
  }
  return Object.freeze({ ...definition })
}

/** Whether a value has the shape of a plugin definition, e.g. a module's default export. */
export function isPluginDefinition(value: unknown): value is PluginDefinition {
  if (typeof value !== 'object' || value === null) {
    return false
  }
  const candidate = value as { id?: unknown; activate?: unknown }
  return (
    typeof candidate.id === 'string' &&
    candidate.id.length <= 100 &&
    ID_PATTERN.test(candidate.id) &&
    (candidate.activate === undefined || typeof candidate.activate === 'function')
  )
}
