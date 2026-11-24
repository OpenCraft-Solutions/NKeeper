import type { Host, HostCredential, ServiceTypeInfo } from './types'

/** A host being edited, as the host editor and the Scan dialog change it (docs/architecture.md §7.14, §7.26). */
export interface ServiceDraft {
  key: string
  id: string | null
  type: string
  label: string
  port: string
  address: string
  settings: Record<string, unknown>
  available: boolean
  disabledTools: string[]
}

export interface CredentialDraft {
  key: string
  id: string | null
  label: string
  username: string
  domain: string
  notes: string
  hasPassword: boolean
  hasPrivateKey: boolean
  password: string
  clearPassword: boolean
  privateKey: string
  clearPrivateKey: boolean
  allServices: boolean
  serviceKeys: string[]
}

export interface Draft {
  name: string
  hostname: string
  ipAddress: string
  description: string
  macAddress: string
  cpuCores: string
  /** In GiB, as 1.x's editor showed it. */
  memoryGiB: string
  serialNumber: string
  isVirtualMachine: boolean
  /** The host it runs on, when it's a virtual machine; '' for unknown. */
  hypervisorId: string
  tags: { key: string; name: string; value: string }[]
  services: ServiceDraft[]
  credentials: CredentialDraft[]
  jump: { hostId: string; serviceId: string; credentialId: string } | null
}

let keys = 0
export const newKey = () => `k${++keys}`

export function draftOf(host: Host | null): Draft {
  if (!host) {
    return {
      name: '',
      hostname: '',
      ipAddress: '',
      description: '',
      macAddress: '',
      cpuCores: '',
      memoryGiB: '',
      serialNumber: '',
      isVirtualMachine: false,
      hypervisorId: '',
      tags: [],
      services: [],
      credentials: [],
      jump: null,
    }
  }
  const services = host.services.map(
    (s): ServiceDraft => ({
      key: newKey(),
      id: s.id,
      type: s.type,
      label: s.label ?? '',
      port: s.port?.toString() ?? '',
      address: s.address ?? '',
      settings: { ...s.settings },
      available: s.available,
      disabledTools: [...s.disabledTools],
    }),
  )
  return {
    name: host.name,
    hostname: host.hostname ?? '',
    ipAddress: host.ipAddress ?? '',
    description: host.description ?? '',
    macAddress: host.macAddress ?? '',
    cpuCores: host.cpuCores?.toString() ?? '',
    memoryGiB: host.memoryMiB ? String(host.memoryMiB / 1024) : '',
    serialNumber: host.serialNumber ?? '',
    isVirtualMachine: host.isVirtualMachine,
    hypervisorId: host.hypervisorId ?? '',
    tags: host.tags.map((t) => ({ key: newKey(), name: t.name, value: t.value ?? '' })),
    services,
    credentials: host.credentials.map((c) => credentialDraft(c, services)),
    jump: host.jump ? { hostId: host.jump.hostId, serviceId: host.jump.serviceId ?? '', credentialId: host.jump.credentialId ?? '' } : null,
  }
}

export function credentialDraft(c: HostCredential, services: ServiceDraft[]): CredentialDraft {
  return {
    key: newKey(),
    id: c.id,
    label: c.label ?? '',
    username: c.username ?? '',
    domain: c.domain ?? '',
    notes: c.notes ?? '',
    hasPassword: c.hasPassword,
    hasPrivateKey: c.hasPrivateKey,
    password: '',
    clearPassword: false,
    privateKey: '',
    clearPrivateKey: false,
    allServices: c.serviceIndexes === null,
    serviceKeys: (c.serviceIndexes ?? []).map((i) => services[i]?.key).filter((k): k is string => !!k),
  }
}

export const orNull = (value: string) => (value.trim() === '' ? null : value.trim())

/** The request body: services in order, and each credential's services as positions in that order. */
export function requestOf(draft: Draft, version: number | null) {
  const positions = new Map(draft.services.map((s, i) => [s.key, i]))
  return {
    name: draft.name.trim(),
    hostname: orNull(draft.hostname),
    ipAddress: orNull(draft.ipAddress),
    description: orNull(draft.description),
    macAddress: orNull(draft.macAddress),
    cpuCores: draft.cpuCores.trim() === '' ? null : Number(draft.cpuCores),
    memoryMiB: draft.memoryGiB.trim() === '' ? null : Math.round(Number(draft.memoryGiB) * 1024),
    serialNumber: orNull(draft.serialNumber),
    isVirtualMachine: draft.isVirtualMachine,
    hypervisorId: draft.isVirtualMachine && draft.hypervisorId ? draft.hypervisorId : null,
    tags: draft.tags.filter((t) => t.name.trim() !== '').map((t) => ({ name: t.name.trim(), value: orNull(t.value) })),
    services: draft.services.map((s) => ({
      id: s.id,
      type: s.type,
      label: orNull(s.label),
      port: s.port.trim() === '' ? null : Number(s.port),
      address: orNull(s.address),
      settings: s.settings,
      disabledTools: s.disabledTools,
    })),
    credentials: draft.credentials.map((c) => ({
      id: c.id,
      label: orNull(c.label),
      username: orNull(c.username),
      domain: orNull(c.domain),
      notes: orNull(c.notes),
      password: c.password === '' ? null : c.password,
      clearPassword: c.clearPassword,
      privateKey: c.privateKey.trim() === '' ? null : c.privateKey,
      clearPrivateKey: c.clearPrivateKey,
      serviceIndexes: c.allServices ? null : c.serviceKeys.map((k) => positions.get(k)).filter((i): i is number => i !== undefined),
    })),
    jump: draft.jump && draft.jump.hostId ? { hostId: draft.jump.hostId, serviceId: draft.jump.serviceId || null, credentialId: draft.jump.credentialId || null } : null,
    ...(version === null ? {} : { version }),
  }
}

/** A new service of a type: a required switch shows as off, so it starts as off rather than unset. */
export function newServiceDraft(type: ServiceTypeInfo, port = ''): ServiceDraft {
  return {
    key: newKey(),
    id: null,
    type: type.id,
    label: '',
    port,
    address: '',
    settings: Object.fromEntries(type.settings.filter((s) => s.kind === 'switch' && s.required).map((s) => [s.key, false])),
    available: true,
    disabledTools: [],
  }
}

/** A new credential, used with every service. */
export function newCredentialDraft(username = ''): CredentialDraft {
  return {
    key: newKey(),
    id: null,
    label: '',
    username,
    domain: '',
    notes: '',
    hasPassword: false,
    hasPrivateKey: false,
    password: '',
    clearPassword: false,
    privateKey: '',
    clearPrivateKey: false,
    allServices: true,
    serviceKeys: [],
  }
}
