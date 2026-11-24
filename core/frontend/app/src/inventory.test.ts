import { describe, expect, it } from 'vitest'
import { buildInventory, inventoryCsv } from './inventory'
import { host, service } from './test/fixtures'
import type { ServiceTypeInfo } from './types'

const types = new Map<string, ServiceTypeInfo>([
  ['ssh', { id: 'ssh', label: 'SSH', defaultPort: 22, settingsVersion: 1, health: 'tcp', settings: [] }],
  ['desktop', { id: 'desktop', label: 'Remote Desktop', defaultPort: 3389, settingsVersion: 1, health: 'tcp', settings: [] }],
])

const hosts = [
  host({ id: 'a', name: 'web01', tags: [{ name: 'site', value: 'DC-A' }], services: [service({ id: 's1', type: 'ssh' })] }),
  host({ id: 'b', name: 'web02', tags: [{ name: 'Site', value: 'dc-a' }], services: [service({ id: 's2', type: 'ssh' }), service({ id: 's3', type: 'desktop' })] }),
  host({ id: 'c', name: 'db01', tags: [{ name: 'site', value: 'DC-B' }], services: [] }),
  host({ id: 'd', name: 'lab01', tags: [], services: [service({ id: 's4', type: 'ssh' })] }),
]

describe('the inventory report (1.x)', () => {
  it('counts each combination, a host with two values under each, and hosts without one last', () => {
    const report = buildInventory(hosts, [{ kind: 'tag', name: 'site' }, { kind: 'service' }], types)

    expect(report.rows.map((r) => [...r.values.map((v) => v.label), r.hosts.map((h) => h.name).join(',')])).toEqual([
      ['DC-A', 'Remote Desktop', 'web02'],
      ['DC-A', 'SSH', 'web01,web02'],
      ['DC-B', 'No Service type', 'db01'],
      ['No site', 'SSH', 'lab01'],
    ])
    expect(report.hostCount).toBe(4)
  })

  it('downloads as CSV, with formula characters neutralized', () => {
    const report = buildInventory([...hosts, host({ id: 'e', name: '=cmd', tags: [{ name: 'site', value: 'DC-B' }] })], [{ kind: 'tag', name: 'site' }], types)
    const rows = inventoryCsv(report).trimEnd().split('\r\n')

    expect(rows[0]).toBe('site,Hosts,Host names')
    expect(rows).toContain("DC-B,2,'=cmd; db01")
    expect(rows.at(-1)).toBe(',1,lab01')
  })
})
