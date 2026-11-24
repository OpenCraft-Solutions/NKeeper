import type { Host } from './types'

/** Hosts that look like the same machine, and why (docs/architecture.md §7.46), ported from 1.x. */
export interface DuplicateGroup {
  /** Stable while the hosts are the same: their IDs, sorted. */
  id: string
  /** The one offered to keep first: the one with the most in it. */
  hosts: Host[]
  /** e.g. "Same MAC address 00:11:22:33:44:55". */
  reasons: string[]
}

type Field = 'mac' | 'ip' | 'hostname' | 'name'

const mac = (h: Host) => h.macAddress?.trim().toLowerCase().replaceAll('-', ':') || null
const ip = (h: Host) => h.ipAddress?.trim().toLowerCase() || null
/** The hostname without its domain (sql01.corp.lan and SQL01 match); an IP-looking hostname as it is. */
const hostname = (h: Host) => {
  const value = h.hostname?.trim().toLowerCase()
  if (!value) {
    return null
  }
  return /^[\d.]+$/.test(value) || value.includes(':') ? value : (value.split('.')[0] ?? value)
}
const name = (h: Host) => h.name.trim().toLowerCase() || null

const FIELDS: { field: Field; read: (h: Host) => string | null; label: string }[] = [
  { field: 'mac', read: mac, label: 'MAC address' },
  { field: 'ip', read: ip, label: 'IP address' },
  { field: 'hostname', read: hostname, label: 'hostname' },
  { field: 'name', read: name, label: 'name' },
]

/**
 * Whether two hosts are the same machine, and by what. The first field both have decides, in the order MAC address,
 * IP address, hostname, name: two hosts with different MAC addresses are different machines even when their names
 * match. Hosts reached through different jump hosts can share a private address, so for them only a MAC address counts.
 */
export function sameMachine(a: Host, b: Host): { label: string; value: string } | null {
  for (const { field, read, label } of FIELDS) {
    const x = read(a)
    const y = read(b)
    if (x === null || y === null) {
      continue
    }
    if (field !== 'mac' && (a.jump?.hostId ?? null) !== (b.jump?.hostId ?? null)) {
      return null
    }
    // Shown as one of them has it (the name and hostname with their capitals).
    const shown = field === 'name' ? a.name : field === 'hostname' ? (a.hostname ?? x) : x
    return x === y ? { label, value: shown } : null
  }
  return null
}

/** Groups of hosts that look like duplicates, most hosts first, then by name. */
export function findDuplicates(hosts: Host[]): DuplicateGroup[] {
  // Only hosts sharing a value can match: compare within each value's bucket.
  const buckets = new Map<string, Host[]>()
  for (const host of hosts) {
    for (const { field, read } of FIELDS) {
      const value = read(host)
      if (value) {
        const key = `${field}:${value}`
        buckets.set(key, [...(buckets.get(key) ?? []), host])
      }
    }
  }

  const parent = new Map(hosts.map((h) => [h.id, h.id]))
  const root = (id: string): string => {
    let current = id
    while (parent.get(current) !== current) {
      current = parent.get(current)!
    }
    return current
  }
  const reasons = new Map<string, Set<string>>()
  const compared = new Set<string>()
  for (const bucket of buckets.values()) {
    for (let i = 0; i < bucket.length; i++) {
      for (let j = i + 1; j < bucket.length; j++) {
        const a = bucket[i]!
        const b = bucket[j]!
        const pair = a.id < b.id ? `${a.id}|${b.id}` : `${b.id}|${a.id}`
        if (compared.has(pair)) {
          continue
        }
        compared.add(pair)
        const match = sameMachine(a, b)
        if (match) {
          const ra = root(a.id)
          const rb = root(b.id)
          parent.set(ra, rb)
          reasons.set(rb, new Set([...(reasons.get(ra) ?? []), ...(reasons.get(rb) ?? []), `Same ${match.label} ${match.value}`]))
        }
      }
    }
  }

  const groups = new Map<string, Host[]>()
  for (const host of hosts) {
    const r = root(host.id)
    groups.set(r, [...(groups.get(r) ?? []), host])
  }
  return [...groups.entries()]
    .filter(([, members]) => members.length > 1)
    .map(([r, members]) => ({
      id: members
        .map((h) => h.id)
        .sort()
        .join(','),
      hosts: [...members].sort((a, b) => richness(b) - richness(a) || a.createdAtUtc.localeCompare(b.createdAtUtc)),
      reasons: [...(reasons.get(r) ?? [])],
    }))
    .sort((a, b) => b.hosts.length - a.hosts.length || a.hosts[0]!.name.localeCompare(b.hosts[0]!.name))
}

/** How much a host holds: the one with the most is offered as the one to keep. */
export function richness(host: Host): number {
  const fields = [host.hostname, host.ipAddress, host.macAddress, host.description, host.serialNumber, host.cpuCores, host.memoryMiB]
  return fields.filter((f) => f != null && f !== '').length + host.services.length * 2 + host.credentials.length * 2 + host.tags.length
}
