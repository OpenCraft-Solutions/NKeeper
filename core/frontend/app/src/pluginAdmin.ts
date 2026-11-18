import { useCallback, useEffect, useState } from 'react'
import { api, ApiError } from '@nkeeper/sdk'

export interface CatalogCandidate {
  id: string
  name: string
  version: string
  packageSha256: string
  keyId: string
  builtIn: boolean
  dependsOn: string[]
}

export interface CatalogPluginStatus {
  id: string
  repository: string
  summary: string | null
  installedVersion: string | null
  available: CatalogCandidate | null
  reasonCode: string | null
  message: string | null
}

export interface CatalogListing {
  catalog: string
  official: boolean
  status: CatalogPluginStatus
}

export interface CatalogSource {
  url: string
  official: boolean
  error: string | null
  warnings: string[]
}

export interface TrustedKey {
  keyId: string
  allowedIds: string[]
  comment: string | null
  notAfter: string | null
}

export interface RequestResult {
  id: string
  version: string | null
  result: string
  message: string
  requestedBy: string | null
  at: string
}

/** Plugin administration as the server reports it (docs/architecture.md §9.1–§9.3). */
export interface PluginAdmin {
  enabled: boolean
  canRequest: boolean
  canTrust: boolean
  refreshedAt: string | null
  catalogs: CatalogSource[]
  plugins: CatalogListing[]
  conflicts: string[]
  switches: Record<string, boolean>
  keys: TrustedKey[]
  pending: string[]
  results: RequestResult[]
}

export interface UploadResponse {
  id: string | null
  version: string | null
  keyId: string | null
  requested: boolean
  reasonCode: string | null
  message: string
}

export const errorMessage = (error: unknown, fallback: string) => (error instanceof ApiError ? error.message : fallback)

/** Loads plugin administration, and looks again now and then while root's helper has requests to apply. */
export function usePluginAdmin() {
  const [admin, setAdmin] = useState<PluginAdmin | null>(null)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(() => {
    api.get<PluginAdmin>('/api/plugins/catalog').then(
      (a) => {
        setAdmin(a)
        setError(null)
      },
      (e: unknown) => setError(errorMessage(e, 'Couldn’t read the plugin settings.')),
    )
  }, [])

  useEffect(load, [load])

  useEffect(() => {
    if (!admin || admin.pending.length === 0) {
      return
    }
    const timer = window.setInterval(load, 10_000)
    return () => window.clearInterval(timer)
  }, [admin, load])

  return { admin, setAdmin, error, setError, reload: load }
}
