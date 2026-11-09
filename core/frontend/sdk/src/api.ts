/**
 * The HTTP client for the NKeeper API, shared by the shell and plugins (ported from 1.x).
 *
 * - Requests are same-origin; authentication is an HttpOnly cookie the server manages, so nothing
 *   secret is ever held in JavaScript.
 * - Unsafe requests carry an antiforgery token in a header, fetched lazily, kept in memory only, and
 *   refreshed once if the server reports it invalid. (The token endpoint arrives with sign-in in Phase 1.)
 * - Every failure is an {@link ApiError}, including network failures (status 0).
 */

const CSRF_ERROR_CODE = 'csrf_token_invalid'

/** RFC 7807 problem details, as the server returns them. */
export interface ProblemDetails {
  type?: string
  title?: string
  status?: number
  detail?: string
  code?: string
  errors?: Record<string, string[]>
  traceId?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly problem?: ProblemDetails
  readonly retryAfterSeconds?: number

  constructor(status: number, message: string, problem?: ProblemDetails, retryAfterSeconds?: number) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.problem = problem
    this.retryAfterSeconds = retryAfterSeconds
  }

  get code(): string | undefined {
    return this.problem?.code
  }

  get isNetworkError(): boolean {
    return this.status === 0
  }
}

type HttpMethod = 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'

export interface RequestOptions {
  /** Serialized as JSON, except FormData (sent as multipart). */
  body?: unknown
  /** 'blob' returns the body as a Blob, 'stream' the Response itself; JSON otherwise. */
  responseType?: 'json' | 'blob' | 'stream'
  signal?: AbortSignal
  /** Don't notify unauthorized listeners on 401 (a failed sign-in isn't an expired session). */
  suppressUnauthorizedEvent?: boolean
}

interface AntiforgeryToken {
  token: string
  headerName: string
}

let antiforgeryToken: Promise<AntiforgeryToken> | null = null
const unauthorizedListeners = new Set<() => void>()

/** Antiforgery tokens are bound to the signed-in user: call this after sign-in and sign-out. */
export function resetAntiforgeryToken(): void {
  antiforgeryToken = null
}

/** Subscribes to 401 responses (session expired or revoked). Returns an unsubscribe function. */
export function onUnauthorized(listener: () => void): () => void {
  unauthorizedListeners.add(listener)
  return () => {
    unauthorizedListeners.delete(listener)
  }
}

function getAntiforgeryToken(): Promise<AntiforgeryToken> {
  antiforgeryToken ??= send<AntiforgeryToken>('GET', '/api/auth/antiforgery', {}).catch((error: unknown) => {
    antiforgeryToken = null
    throw error
  })
  return antiforgeryToken
}

async function send<T>(method: HttpMethod, path: string, options: RequestOptions): Promise<T> {
  const headers: Record<string, string> = { Accept: options.responseType === 'blob' ? '*/*' : 'application/json' }
  const form = options.body instanceof FormData
  if (options.body !== undefined && !form) {
    headers['Content-Type'] = 'application/json'
  }
  if (method !== 'GET') {
    const { token, headerName } = await getAntiforgeryToken()
    headers[headerName] = token
  }

  let response: Response
  try {
    response = await fetch(path, {
      method,
      headers,
      body: options.body === undefined ? undefined : form ? (options.body as FormData) : JSON.stringify(options.body),
      credentials: 'same-origin',
      signal: options.signal,
    })
  } catch (error) {
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw error
    }
    throw new ApiError(0, 'Unable to reach the server. Check your connection and try again.')
  }

  if (response.ok) {
    if (options.responseType === 'stream') {
      return response as T
    }
    if (options.responseType === 'blob') {
      return (await response.blob()) as T
    }
    return (response.status === 204 ? undefined : await response.json()) as T
  }

  const problem = await readProblem(response)
  if (response.status === 401 && !options.suppressUnauthorizedEvent) {
    unauthorizedListeners.forEach((listener) => listener())
  }

  const retryAfter = Number.parseInt(response.headers.get('Retry-After') ?? '', 10)
  throw new ApiError(
    response.status,
    problem?.detail ?? problem?.title ?? `Request failed with status ${response.status}.`,
    problem,
    Number.isFinite(retryAfter) ? retryAfter : undefined,
  )
}

async function readProblem(response: Response): Promise<ProblemDetails | undefined> {
  if (!(response.headers.get('Content-Type') ?? '').includes('json')) {
    return undefined
  }
  try {
    return (await response.json()) as ProblemDetails
  } catch {
    return undefined
  }
}

async function request<T>(method: HttpMethod, path: string, options: RequestOptions = {}): Promise<T> {
  try {
    return await send<T>(method, path, options)
  } catch (error) {
    // A stale antiforgery token (the session changed in another tab): refresh and retry once.
    if (method !== 'GET' && error instanceof ApiError && error.status === 400 && error.code === CSRF_ERROR_CODE) {
      resetAntiforgeryToken()
      return send<T>(method, path, options)
    }
    throw error
  }
}

/** Calls the NKeeper API. Paths are absolute, e.g. `/api/plugins` or `/api/p/<id>/…`. */
export const api = {
  get: <T>(path: string, options?: RequestOptions) => request<T>('GET', path, options),
  post: <T>(path: string, body?: unknown, options?: RequestOptions) => request<T>('POST', path, { ...options, body }),
  put: <T>(path: string, body?: unknown, options?: RequestOptions) => request<T>('PUT', path, { ...options, body }),
  patch: <T>(path: string, body?: unknown, options?: RequestOptions) => request<T>('PATCH', path, { ...options, body }),
  delete: <T>(path: string, options?: RequestOptions) => request<T>('DELETE', path, options),
}

/** An absolute ws(s):// URL for an API path, on the page's own origin. */
export function apiWebSocketUrl(path: string): string {
  const url = new URL(path, window.location.origin)
  url.protocol = url.protocol === 'https:' ? 'wss:' : 'ws:'
  return url.toString()
}

/** Hands over each line of JSON in a streamed response as it arrives. */
export async function readJsonLines<T>(response: Response, onLine: (line: T) => void): Promise<void> {
  if (!response.body) {
    return
  }
  const reader = response.body.pipeThrough(new TextDecoderStream()).getReader()
  let text = ''
  for (let next = await reader.read(); !next.done; next = await reader.read()) {
    const lines = (text + next.value).split('\n')
    text = lines.pop() ?? ''
    lines.filter((line) => line.trim()).forEach((line) => onLine(JSON.parse(line) as T))
  }
  // JSON Lines allows the last record to end without a newline.
  if (text.trim()) {
    onLine(JSON.parse(text) as T)
  }
}
