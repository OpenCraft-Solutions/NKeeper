import type { Host } from './types'

/* 1.x's VM hosts & resources report (docs/architecture.md §7.49). */

/** A host that runs VMs, with them and what they're given in total. */
export interface HypervisorGroup {
  host: Host
  guests: Host[]
  /** vCPUs given to its VMs (those with a known count). */
  allocatedCpus: number
  /** Memory given to its VMs, in MiB (those with a known amount). */
  allocatedMemoryMiB: number
  /** VMs whose vCPUs or memory aren't known, so the totals leave them out. */
  incomplete: number
}

export interface VmResourcesReport {
  hypervisors: HypervisorGroup[]
  /** Physical hosts that run no VMs NKeeper knows of. */
  physical: Host[]
  /** VMs whose host isn't known (or isn't visible to the user). */
  unplaced: Host[]
}

const byName = (a: Host, b: Host) => a.name.localeCompare(b.name, undefined, { numeric: true, sensitivity: 'base' })

/**
 * Hosts arranged by what they run on: each hypervisor (any host with VMs linked to it, nested ones
 * included) followed by its VMs, then physical hosts without VMs, then VMs whose host is unknown.
 */
export function buildVmResources(hosts: Host[]): VmResourcesReport {
  const ids = new Set(hosts.map((h) => h.id))
  const guestsOf = new Map<string, Host[]>()
  for (const host of hosts) {
    if (host.hypervisorId && ids.has(host.hypervisorId)) {
      guestsOf.set(host.hypervisorId, [...(guestsOf.get(host.hypervisorId) ?? []), host])
    }
  }

  const hypervisors = hosts
    .filter((h) => guestsOf.has(h.id))
    .sort(byName)
    .map((host): HypervisorGroup => {
      const guests = guestsOf.get(host.id)!.sort(byName)
      return {
        host,
        guests,
        allocatedCpus: guests.reduce((sum, g) => sum + (g.cpuCores ?? 0), 0),
        allocatedMemoryMiB: guests.reduce((sum, g) => sum + (g.memoryMiB ?? 0), 0),
        incomplete: guests.filter((g) => g.cpuCores === null || g.memoryMiB === null).length,
      }
    })

  return {
    hypervisors,
    physical: hosts.filter((h) => !h.isVirtualMachine && !guestsOf.has(h.id)).sort(byName),
    unplaced: hosts.filter((h) => h.isVirtualMachine && !(h.hypervisorId && ids.has(h.hypervisorId)) && !guestsOf.has(h.id)).sort(byName),
  }
}

/** A share of a host's capacity as a whole percentage, or null when either side is unknown. */
export function share(part: number | null, whole: number | null): number | null {
  return part !== null && whole ? Math.round((part / whole) * 100) : null
}

/** The report as CSV, one row per host: hypervisors, each followed by its VMs, then physical hosts and unplaced VMs. */
export function vmResourcesCsv(report: VmResourcesReport): string {
  // A value a spreadsheet would run as a formula is kept as text (as the audit and inventory CSVs do).
  const quote = (value: string) => {
    const safe = /^[=+\-@\t\r]/.test(value) ? `'${value}` : value
    return /[",\r\n]/.test(safe) ? `"${safe.replaceAll('"', '""')}"` : safe
  }
  const gib = (mib: number | null) => (mib === null ? '' : String(Math.round((mib / 1024) * 100) / 100))
  const number = (value: number | null) => (value === null ? '' : String(value))
  const rows: string[][] = [
    ['Host', 'Kind', 'Runs on', 'Serial number', 'CPU cores / vCPUs', 'vCPUs allocated', '% of host CPU', 'Memory (GiB)', 'Memory allocated (GiB)', '% of host memory'],
  ]
  const hostRow = (host: Host, kind: string, group?: HypervisorGroup) => [
    host.name,
    kind,
    '',
    host.serialNumber ?? '',
    number(host.cpuCores),
    group ? String(group.allocatedCpus) : '',
    group ? number(share(group.allocatedCpus, host.cpuCores)) : '',
    gib(host.memoryMiB),
    group ? gib(group.allocatedMemoryMiB) : '',
    group ? number(share(group.allocatedMemoryMiB, host.memoryMiB)) : '',
  ]
  for (const group of report.hypervisors) {
    rows.push(hostRow(group.host, group.host.isVirtualMachine ? 'Hypervisor (VM)' : 'Hypervisor', group))
    for (const guest of group.guests) {
      rows.push([
        guest.name,
        'VM',
        group.host.name,
        '',
        number(guest.cpuCores),
        '',
        number(share(guest.cpuCores, group.host.cpuCores)),
        gib(guest.memoryMiB),
        '',
        number(share(guest.memoryMiB, group.host.memoryMiB)),
      ])
    }
  }
  report.physical.forEach((host) => rows.push(hostRow(host, 'Physical')))
  report.unplaced.forEach((host) => rows.push(hostRow(host, 'VM (host unknown)')))
  return rows.map((row) => row.map(quote).join(',')).join('\r\n') + '\r\n'
}
