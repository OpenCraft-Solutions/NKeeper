import type { Host, HostCredential, HostService, ServiceHealth, ServiceTypeInfo } from './types'

/** Searching, grouping and describing hosts, in the browser (docs/architecture.md §7.14). */

/** The address the shell shows for a host: its IP address over its hostname. */
export function hostAddress(host: Host): string | null {
  return host.ipAddress ?? host.hostname
}

/**
 * Whether a host matches every word of a search, ignoring case. A word matches the name, hostname, IP
 * address or description, or a tag as `name` or `name=value`.
 */
export function matchesSearch(host: Host, query: string): boolean {
  const words = query.toLowerCase().split(/\s+/).filter(Boolean)
  if (words.length === 0) {
    return true
  }
  const fields = [host.name, host.hostname, host.ipAddress, host.description].filter((f): f is string => !!f).map((f) => f.toLowerCase())
  const tags = host.tags.map((t) => (t.value === null ? t.name : `${t.name}=${t.value}`).toLowerCase())
  return words.every((word) => fields.some((f) => f.includes(word)) || tags.some((t) => t.includes(word)))
}

export interface HostGroup {
  key: string
  label: string
  hosts: Host[]
}

/** Every tag name the hosts use, sorted. */
export function tagNames(hosts: Host[]): string[] {
  return [...new Set(hosts.flatMap((h) => h.tags.map((t) => t.name)))].sort((a, b) => a.localeCompare(b))
}

/**
 * Hosts grouped by a tag name: one group per value, sorted, then "No <name>" for hosts without it. A host
 * with two values is in both groups. With no tag name, one group of every host.
 */
export function groupHosts(hosts: Host[], tagName: string | null): HostGroup[] {
  if (!tagName) {
    return [{ key: '', label: 'All hosts', hosts }]
  }
  const byValue = new Map<string, Host[]>()
  const without: Host[] = []
  for (const host of hosts) {
    const values = [...new Set(host.tags.filter((t) => t.name.toLowerCase() === tagName.toLowerCase()).map((t) => t.value ?? ''))]
    if (values.length === 0) {
      without.push(host)
    }
    for (const value of values) {
      byValue.set(value, [...(byValue.get(value) ?? []), host])
    }
  }
  const groups = [...byValue.entries()]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([value, members]) => ({ key: `=${value}`, label: value === '' ? `${tagName} (no value)` : value, hosts: members }))
  return without.length > 0 ? [...groups, { key: '!', label: `No ${tagName}`, hosts: without }] : groups
}

/** A service as the shell names it: its label, else its type and port. */
export function serviceName(service: HostService, types: Map<string, ServiceTypeInfo>): string {
  if (service.label) {
    return service.label
  }
  const type = types.get(service.type)
  const port = service.port ?? type?.defaultPort
  return `${type?.label ?? service.type}${port ? ` :${port}` : ''}`
}

/** A credential as the shell names it: its label, else who it signs in as. */
export function credentialName(credential: HostCredential): string {
  if (credential.label) {
    return credential.label
  }
  if (credential.username) {
    return credential.domain ? `${credential.domain}\\${credential.username}` : credential.username
  }
  return 'Unnamed credential'
}

/** Whether a credential is used with the service at `index`. */
export function usedWith(credential: HostCredential, index: number): boolean {
  return credential.serviceIndexes === null || credential.serviceIndexes.includes(index)
}

export type HealthSummary = 'up' | 'down' | 'partial' | 'unknown'

/** One word for a host's services' health: all up, all down, some down, or nothing known. */
export function healthSummary(host: Host, health: Map<string, ServiceHealth>): HealthSummary {
  const known = host.services.map((s) => health.get(s.id)).filter((h): h is ServiceHealth => !!h)
  if (known.length === 0) {
    return 'unknown'
  }
  const down = known.filter((h) => h.state === 'down').length
  return down === 0 ? 'up' : down === known.length ? 'down' : 'partial'
}
