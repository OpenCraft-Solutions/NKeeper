import type { ColorSchemePreferences } from './appearance/colorSchemes'

/** The API's shapes, as the shell reads them (docs/architecture.md §6, §7). */

/** The signed-in user, as `/api/auth/login` and `/api/auth/me` return them. */
export interface CurrentUser {
  id: string
  userName: string
  email: string | null
  roles: string[]
  mustChangePassword: boolean
  /** The user's colour scheme and the schemes they built (docs/architecture.md §6.1). */
  colorSchemes: ColorSchemePreferences
}

export const ADMINISTRATOR = 'Administrator'

export interface ServiceSetting {
  key: string
  label: string
  kind: 'text' | 'number' | 'switch' | 'choice'
  required: boolean
  maxLength: number | null
  minimum: number | null
  maximum: number | null
  choices: string[] | null
}

export interface ServiceTypeInfo {
  id: string
  label: string
  defaultPort: number | null
  settingsVersion: number
  settings: ServiceSetting[]
  /** The kind of its health probe, e.g. `tcp`; null when it has none. */
  health: string | null
}

export interface SessionTypeInfo {
  id: string
  label: string
  serviceTypes: string[]
  supportsJumpHost: boolean
}

export interface HostActionInfo {
  id: string
  label: string
  /** The session type it opens; null for a run action. */
  sessionType: string | null
  /** For a run action: its path in the plugin's host group. */
  endpoint: string | null
  /** For a run action: the service types it applies to. */
  serviceTypes: string[]
  credential: 'required' | 'none'
}

export interface HostTag {
  name: string
  value: string | null
}

export interface HostService {
  id: string
  type: string
  label: string | null
  port: number | null
  address: string | null
  settings: Record<string, unknown>
  /** Whether an enabled plugin provides the type. */
  available: boolean
}

export interface HostCredential {
  id: string
  label: string | null
  username: string | null
  domain: string | null
  hasPassword: boolean
  hasPrivateKey: boolean
  notes: string | null
  /** Positions in the host's services; null for all of them. */
  serviceIndexes: number[] | null
}

export interface HostJump {
  hostId: string
  serviceId: string | null
  credentialId: string | null
}

export interface TrustedKey {
  id: string
  kind: string
  address: string | null
  port: number
  algorithm: string
  fingerprint: string
  trustedAtUtc: string
}

export interface Host {
  id: string
  name: string
  hostname: string | null
  ipAddress: string | null
  description: string | null
  tags: HostTag[]
  services: HostService[]
  credentials: HostCredential[]
  jump: HostJump | null
  trustedKeys: TrustedKey[]
  createdAtUtc: string
  updatedAtUtc: string
  version: number
  canManage: boolean
}

export interface ServiceHealth {
  hostId: string
  serviceId: string
  state: 'up' | 'down'
  latencyMs: number
  checkedAtUtc: string
  reason: 'refused' | 'timeout' | 'unreachable' | null
  /** For a TLS probe that completed its handshake. */
  certificateSubject?: string | null
  certificateExpiresAtUtc?: string | null
}
