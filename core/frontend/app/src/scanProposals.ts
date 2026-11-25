import { newCredentialDraft, newServiceDraft, type Draft } from './hostDraft'
import type { Host, ScanFindings, ScannedGuest, ServiceTypeInfo } from './types'

/** The tag a scan records the operating system in, e.g. `os: Ubuntu 24.04.1 LTS`, as 1.x did. */
export const OS_TAG = 'os'

/** Found accounts start ticked when there are at most this many new ones: a server with many rarely needs each. */
export const USERS_PRESELECTED_UP_TO = 10

/** A host a scan could add for a neighbour that matches none the user can see. */
export interface ProposedHost {
  name: string
  ipAddress: string
  macAddress: string | null
  description: string
  /** For a hypervisor's guest (§7.49): a virtual machine running on it, with what it's given. */
  isVirtualMachine?: boolean
  hypervisorId?: string | null
  cpuCores?: number | null
  memoryMiB?: number | null
}

/**
 * A change a scan suggests (docs/architecture.md §7.26). Applying one edits the host's draft, or adds a host; nothing
 * is saved until the user applies. Ones that fill a gap start ticked; ones that replace a value start unticked.
 */
export interface ScanProposal {
  key: string
  group: 'Host' | 'Services' | 'Logins' | 'Virtual machines' | 'New hosts'
  label: string
  detail: string
  checked: boolean
  apply?: (draft: Draft) => void
  newHost?: ProposedHost
  /** A change to another host: a guest found on this hypervisor, linked as running on it (§7.49). */
  other?: { host: Host; apply: (draft: Draft) => void }
}

const sameText = (a: string | null | undefined, b: string | null | undefined) => (a ?? '').toLowerCase() === (b ?? '').toLowerCase()
const now = (current: string | null | undefined) => (current ? ` (now ${current})` : '')

export function formatMemory(mib: number): string {
  return mib >= 1024 ? `${Math.round((mib / 1024) * 10) / 10} GiB` : `${mib} MiB`
}

/** The service type a port is: the scanner's, when a plugin provides it, or else the one type whose default port it is. */
function typeOfPort(port: { port: number; serviceType?: string | null }, serviceTypes: Map<string, ServiceTypeInfo>): ServiceTypeInfo | null {
  if (port.serviceType) {
    return serviceTypes.get(port.serviceType) ?? null
  }
  const candidates = [...serviceTypes.values()].filter((t) => t.defaultPort === port.port)
  return candidates.length === 1 ? candidates[0]! : null
}

/**
 * Turns a scan's findings into proposed changes, leaving out what the host already has. Ported from 1.x's
 * `scanProposals`, without guests and cluster nodes, which wait for the virt plugins.
 * @param hosts Every host the user can see, to match neighbours against.
 * @param canCreateHosts Whether the user may add hosts (administrators), for neighbours that match none.
 */
export function scanProposals(
  host: Host,
  findings: ScanFindings,
  serviceTypes: Map<string, ServiceTypeInfo>,
  hosts: Host[],
  canCreateHosts: boolean,
): ScanProposal[] {
  const proposals: ScanProposal[] = []
  const field = (key: string, label: string, found: string | null | undefined, current: string | null, apply: (draft: Draft, value: string) => void, same = sameText) => {
    if (found && !same(found, current)) {
      proposals.push({ key, group: 'Host', label, detail: found + now(current), checked: !current, apply: (draft) => apply(draft, found) })
    }
  }

  field('hostname', 'Hostname', findings.hostname, host.hostname, (d, v) => void (d.hostname = v))
  field('ip', 'IP address', findings.ipAddress, host.ipAddress, (d, v) => void (d.ipAddress = v))
  field('mac', 'MAC address', findings.macAddress?.toUpperCase(), host.macAddress, (d, v) => void (d.macAddress = v))

  if (findings.virtualization && !host.isVirtualMachine) {
    proposals.push({ key: 'vm', group: 'Host', label: 'Virtual machine', detail: `it runs under ${findings.virtualization}`, checked: true, apply: (d) => void (d.isVirtualMachine = true) })
  }

  const cpus = findings.cpuCores
  if (cpus && cpus !== host.cpuCores) {
    proposals.push({
      key: 'cpus',
      group: 'Host',
      label: 'CPU cores',
      detail: `${cpus}${now(host.cpuCores ? String(host.cpuCores) : null)}`,
      checked: !host.cpuCores,
      apply: (d) => void (d.cpuCores = String(cpus)),
    })
  }

  const mib = findings.memoryMiB
  if (mib && mib !== host.memoryMiB) {
    proposals.push({
      key: 'memory',
      group: 'Host',
      label: 'Memory',
      detail: formatMemory(mib) + now(host.memoryMiB ? formatMemory(host.memoryMiB) : null),
      checked: !host.memoryMiB,
      apply: (d) => void (d.memoryGiB = String(mib / 1024)),
    })
  }

  field('serial', 'Serial number', findings.serialNumber?.slice(0, 100), host.serialNumber, (d, v) => void (d.serialNumber = v), (a, b) => a === b)

  const os = findings.operatingSystem?.slice(0, 255)
  if (os && !host.tags.some((t) => sameText(t.name, OS_TAG) && t.value === os)) {
    const current = host.tags.find((t) => sameText(t.name, OS_TAG))?.value
    proposals.push({
      key: 'os',
      group: 'Host',
      label: 'Operating system',
      detail: `${OS_TAG}: ${os}${now(current)}`,
      checked: true,
      apply: (d) => void (d.tags = [...d.tags.filter((t) => !sameText(t.name, OS_TAG)), { key: 'scan-os', name: OS_TAG, value: os }]),
    })
  }

  const proposed = new Set<string>()
  for (const port of findings.ports ?? []) {
    const type = typeOfPort(port, serviceTypes)
    // A type with settings the scan can't fill in (a required text, say) is left for the user to add.
    if (!type || type.settings.some((s) => s.required && s.kind !== 'switch')) {
      continue
    }
    const key = `service-${type.id}-${port.port}`
    if (proposed.has(key) || host.services.some((s) => s.type === type.id && (s.port ?? type.defaultPort) === port.port)) {
      continue
    }
    proposed.add(key)
    const stored = port.port === type.defaultPort ? '' : String(port.port)
    proposals.push({
      key,
      group: 'Services',
      label: type.label,
      detail: `port ${port.port}${port.name && port.name !== type.label ? ` · ${port.name}` : ''}`,
      checked: true,
      apply: (d) => void d.services.push(newServiceDraft(type, stored)),
    })
  }

  const users = (findings.users ?? []).filter((u, i, all) => all.findIndex((o) => sameText(o.username, u.username)) === i && !host.credentials.some((c) => sameText(c.username, u.username)))
  for (const user of users) {
    proposals.push({
      key: `user-${user.username}`,
      group: 'Logins',
      label: user.username,
      detail: user.fullName ? `${user.fullName} · a credential with no password yet` : 'a credential with no password yet',
      checked: users.length <= USERS_PRESELECTED_UP_TO,
      apply: (d) => void d.credentials.push(newCredentialDraft(user.username)),
    })
  }

  proposals.push(...guestProposals(host, findings.guests ?? [], hosts, canCreateHosts))

  if (canCreateHosts) {
    const seen = new Set<string>()
    for (const neighbor of findings.neighbors ?? []) {
      const mac = neighbor.macAddress?.toUpperCase() ?? null
      const known = hosts.some((h) => h.ipAddress === neighbor.ipAddress || (mac !== null && h.macAddress === mac))
      if (known || seen.has(neighbor.ipAddress)) {
        continue
      }
      seen.add(neighbor.ipAddress)
      proposals.push({
        key: `neighbor-${neighbor.ipAddress}`,
        group: 'New hosts',
        label: neighbor.ipAddress,
        detail: mac ?? 'unknown MAC address',
        checked: false,
        newHost: { name: neighbor.ipAddress, ipAddress: neighbor.ipAddress, macAddress: mac, description: `Seen in ${host.name}’s neighbour table.` },
      })
    }
  }

  return proposals
}

const shortName = (name: string | null) => (name ?? '').split('.')[0]!.toLowerCase()

/**
 * 1.x's hypervisor scan (§7.49): each guest is the host with one of its MAC addresses, else one of its addresses, else
 * its name, and is proposed as running here (or on its cluster node), with the vCPUs and memory it's given. A guest
 * no host matches is proposed as a new host, when it has an address. A guest the user can't change is left alone.
 */
function guestProposals(hypervisor: Host, guests: ScannedGuest[], hosts: Host[], canCreateHosts: boolean): ScanProposal[] {
  const proposals: ScanProposal[] = []
  const linked = new Set<string>()
  for (const guest of guests) {
    const node = guest.node ? hosts.find((h) => shortName(h.name) === shortName(guest.node!) || shortName(h.hostname) === shortName(guest.node!)) : hypervisor
    if (!node) {
      continue
    }
    const macs = (guest.macAddresses ?? []).map((m) => m.toUpperCase())
    const ips = guest.ipAddresses ?? []
    const match =
      hosts.find((h) => h.macAddress !== null && macs.includes(h.macAddress)) ??
      hosts.find((h) => h.ipAddress !== null && ips.includes(h.ipAddress)) ??
      hosts.find((h) => sameText(h.name, guest.name) || (h.hostname !== null && shortName(h.hostname) === shortName(guest.name)))
    const given = [guest.cpuCores ? `${guest.cpuCores} vCPU${guest.cpuCores === 1 ? '' : 's'}` : null, guest.memoryMiB ? formatMemory(guest.memoryMiB) : null].filter(Boolean).join(' · ')
    if (match) {
      if (match.id === node.id || linked.has(match.id) || !match.canManage) {
        continue
      }
      linked.add(match.id)
      const moves = match.hypervisorId !== node.id
      const fills = (guest.cpuCores && guest.cpuCores !== match.cpuCores) || (guest.memoryMiB && guest.memoryMiB !== match.memoryMiB)
      if (!moves && match.isVirtualMachine && !fills) {
        continue
      }
      const elsewhere = match.hypervisorId && match.hypervisorId !== node.id ? hosts.find((h) => h.id === match.hypervisorId)?.name ?? 'another host' : null
      proposals.push({
        key: `guest-${match.id}`,
        group: 'Virtual machines',
        label: match.name === guest.name ? match.name : `${match.name} (${guest.name})`,
        detail: [`runs on ${node.name}${elsewhere ? ` (now ${elsewhere})` : ''}`, given].filter(Boolean).join(' · '),
        // Moving a VM that's recorded on another host waits for the user's tick.
        checked: !elsewhere,
        other: {
          host: match,
          apply: (d) => {
            d.isVirtualMachine = true
            d.hypervisorId = node.id
            if (guest.cpuCores) {
              d.cpuCores = String(guest.cpuCores)
            }
            if (guest.memoryMiB) {
              d.memoryGiB = String(guest.memoryMiB / 1024)
            }
          },
        },
      })
    } else if (canCreateHosts && ips[0]) {
      proposals.push({
        key: `guest-new-${guest.name}`,
        group: 'New hosts',
        label: guest.name,
        detail: [`a VM on ${node.name} at ${ips[0]}`, given].filter(Boolean).join(' · '),
        checked: false,
        newHost: {
          name: guest.name.slice(0, 200),
          ipAddress: ips[0],
          macAddress: macs[0] ?? null,
          description: `Found by a scan of ${node.name}.`,
          isVirtualMachine: true,
          hypervisorId: node.id,
          cpuCores: guest.cpuCores ?? null,
          memoryMiB: guest.memoryMiB ?? null,
        },
      })
    }
  }
  return proposals
}
