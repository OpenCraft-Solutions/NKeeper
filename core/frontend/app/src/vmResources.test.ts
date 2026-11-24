import { describe, expect, it } from 'vitest'
import { host } from './test/fixtures'
import { buildVmResources, vmResourcesCsv } from './vmResources'

const vm = (id: string, name: string, hypervisorId: string | null, cpuCores: number | null, memoryMiB: number | null) =>
  host({ id, name, isVirtualMachine: true, hypervisorId, cpuCores, memoryMiB })

const vmHosts = [
  host({ id: 'n1', name: 'node1', cpuCores: 32, memoryMiB: 131072, serialNumber: 'ABC1234' }),
  vm('v2', 'web02', 'n1', 4, 8192),
  vm('v1', 'web01', 'n1', 4, 8192),
  vm('v3', 'db01', 'n1', 16, 65536),
  vm('v4', 'mystery', 'n1', null, null),
  host({ id: 'n2', name: 'small', cpuCores: 4, memoryMiB: 8192, serialNumber: 'XYZ9876' }),
  vm('v5', 'greedy', 'n2', 8, 4096),
  host({ id: 'p1', name: 'nas', cpuCores: 8, memoryMiB: 32768, serialNumber: 'NAS0001' }),
  vm('v6', 'orphan', null, 2, 2048),
]

describe('VM resources (1.x)', () => {
  it('puts each VM under its host, with what they’re given in total', () => {
    const report = buildVmResources(vmHosts)
    expect(report.hypervisors.map((g) => [g.host.name, g.guests.map((v) => v.name), g.allocatedCpus, g.allocatedMemoryMiB, g.incomplete])).toEqual([
      ['node1', ['db01', 'mystery', 'web01', 'web02'], 24, 81920, 1],
      ['small', ['greedy'], 8, 4096, 0],
    ])
    expect(report.physical.map((h) => h.name)).toEqual(['nas'])
    expect(report.unplaced.map((h) => h.name)).toEqual(['orphan'])
  })

  it('exports one row per host, VMs after their host, formulas kept as text', () => {
    const rows = vmResourcesCsv(buildVmResources([...vmHosts, host({ id: 'x', name: '=cmd', cpuCores: 1 })])).trimEnd().split('\r\n')

    expect(rows[0]).toBe('Host,Kind,Runs on,Serial number,CPU cores / vCPUs,vCPUs allocated,% of host CPU,Memory (GiB),Memory allocated (GiB),% of host memory')
    expect(rows[1]).toBe('node1,Hypervisor,,ABC1234,32,24,75,128,80,63')
    expect(rows[2]).toBe('db01,VM,node1,,16,,50,64,,50')
    expect(rows[3]).toBe('mystery,VM,node1,,,,,,,')
    expect(rows).toContain('nas,Physical,,NAS0001,8,,,32,,')
    expect(rows).toContain("'=cmd,Physical,,,1,,,,,")
    expect(rows.at(-1)).toBe('orphan,VM (host unknown),,,2,,,2,,')
  })
})
