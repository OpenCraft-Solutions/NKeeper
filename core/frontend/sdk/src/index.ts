/**
 * @nkeeper/sdk v0: what plugin frontends build against. At run time it is one shared module, served
 * by the shell through the import map, so every plugin and the shell see the same instance.
 */
export { SDK_VERSION, definePlugin, isPluginDefinition } from './plugin'
export type { PluginContext, PluginDefinition, SessionViewProps } from './plugin'
export { ApiError, api, apiWebSocketUrl, onUnauthorized, readJsonLines, resetAntiforgeryToken } from './api'
export type { ProblemDetails, RequestOptions } from './api'
export { SessionError, openStreamSession } from './sessions'
export type { HostKeyPrompt, OpenStreamOptions, StreamConnection, StreamStart, TrustPrompt } from './sessions'
export { adoptStyles, mirrorInjectedStyles } from './styles'
