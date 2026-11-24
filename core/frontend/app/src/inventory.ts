import type { Host, ServiceTypeInfo } from './types'

/** A field the inventory report counts by (docs/architecture.md §7.47): a tag name, or the services' types. */
export type InventoryField = { kind: 'tag'; name: string } | { kind: 'service' }

/** One combination of values, and the hosts that have it. */
export interface InventoryRow {
  key: string
  /** One cell per field, in the fields' order; `missing` for the "No …" value. */
  values: { label: string; missing: boolean }[]
  hosts: Host[]
}

export interface InventoryReport {
  fields: InventoryField[]
  rows: InventoryRow[]
  /** Distinct hosts; rows can add up to more, since a host with two values counts under each. */
  hostCount: number
}

export const fieldLabel = (field: InventoryField) => (field.kind === 'service' ? 'Service type' : field.name)

/** A field's values on a host, each once, case-insensitively, as the case first seen; empty when it has none. */
function valuesOf(host: Host, field: InventoryField, types: Map<string, ServiceTypeInfo>): string[] {
  const raw =
    field.kind === 'service'
      ? host.services.map((s) => types.get(s.type)?.label ?? s.type)
      : host.tags.filter((t) => t.name.toLowerCase() === field.name.toLowerCase()).map((t) => t.value || '(no value)')
  const seen = new Map<string, string>()
  for (const value of raw) {
    if (!seen.has(value.toLowerCase())) {
      seen.set(value.toLowerCase(), value)
    }
  }
  return [...seen.values()]
}

/**
 * Host counts for each combination of the fields' values, as 1.x's inventory report: values match case-insensitively,
 * a host with several values counts under each, and hosts without one fall in a trailing "No …" row at that level.
 */
export function buildInventory(hosts: Host[], fields: InventoryField[], types: Map<string, ServiceTypeInfo>): InventoryReport {
  if (fields.length === 0) {
    return { fields, rows: [], hostCount: 0 }
  }
  const rows: InventoryRow[] = []
  const visit = (members: Host[], level: number, path: InventoryRow['values'], key: string) => {
    if (level === fields.length) {
      rows.push({ key, values: path, hosts: [...members].sort((a, b) => a.name.localeCompare(b.name)) })
      return
    }
    const field = fields[level]!
    const byValue = new Map<string, { label: string; hosts: Host[] }>()
    const without: Host[] = []
    for (const host of members) {
      const values = valuesOf(host, field, types)
      if (values.length === 0) {
        without.push(host)
      }
      for (const value of values) {
        const entry = byValue.get(value.toLowerCase()) ?? { label: value, hosts: [] }
        entry.hosts.push(host)
        byValue.set(value.toLowerCase(), entry)
      }
    }
    for (const [lower, entry] of [...byValue.entries()].sort(([, a], [, b]) => a.label.localeCompare(b.label))) {
      visit(entry.hosts, level + 1, [...path, { label: entry.label, missing: false }], `${key}/=${lower}`)
    }
    if (without.length > 0) {
      visit(without, level + 1, [...path, { label: `No ${fieldLabel(field)}`, missing: true }], `${key}/!`)
    }
  }
  visit(hosts, 0, [], '')
  return { fields, rows, hostCount: hosts.length }
}

/** The report as CSV: a column per field, then the count and the hosts' names. */
export function inventoryCsv(report: InventoryReport): string {
  const quote = (value: string) => {
    const safe = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
    return /[",\r\n]/.test(safe) ? `"${safe.replaceAll('"', '""')}"` : safe
  }
  const rows: string[][] = [
    [...report.fields.map(fieldLabel), 'Hosts', 'Host names'],
    ...report.rows.map((row) => [...row.values.map((v) => (v.missing ? '' : v.label)), String(row.hosts.length), row.hosts.map((h) => h.name).join('; ')]),
  ]
  return rows.map((row) => row.map(quote).join(',')).join('\r\n') + '\r\n'
}
