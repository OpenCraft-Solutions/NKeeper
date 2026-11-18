import { describe, expect, it } from 'vitest'
import { draftOf, requestOf } from './hostDraft'
import { scanProposals } from './scanProposals'
import { credential, host, service } from './test/fixtures'
import type { ServiceTypeInfo } from './types'

const type = (id: string, defaultPort: number | null, required = false): ServiceTypeInfo => ({
  id,
  label: id.toUpperCase(),
  defaultPort,
  settingsVersion: 1,
  health: null,
  settings: required ? [{ key: 'x', label: 'X', kind: 'text', required: true, maxLength: 5, minimum: null, maximum: null, choices: null }] : [],
})
const types = new Map([type('ssh', 22), type('http', 80), type('alt-http', 80), type('db', 5432, true), type('vnc', 5900)].map((t) => [t.id, t]))

describe('scan proposals', () => {
  it('fills gaps ticked, offers replacements unticked, and leaves out what the host has', () => {
    const h = host({ hostname: 'old.example.com', ipAddress: '192.0.2.10', cpuCores: 4, tags: [{ name: 'OS', value: 'Debian 11' }] })
    const proposals = scanProposals(
      h,
      { hostname: 'web01.example.com', ipAddress: '192.0.2.10', macAddress: 'aa:bb:cc:dd:ee:ff', cpuCores: 8, memoryMiB: 16384, operatingSystem: 'Debian 12', virtualization: 'kvm' },
      types,
      [h],
      false,
    )
    expect(proposals.map((p) => [p.key, p.detail, p.checked])).toEqual([
      ['hostname', 'web01.example.com (now old.example.com)', false],
      ['mac', 'AA:BB:CC:DD:EE:FF', true],
      ['vm', 'it runs under kvm', true],
      ['cpus', '8 (now 4)', false],
      ['memory', '16 GiB', true],
      ['os', 'os: Debian 12 (now Debian 11)', true],
    ])

    const draft = draftOf(h)
    proposals.forEach((p) => p.apply?.(draft))
    expect(requestOf(draft, 1)).toMatchObject({
      hostname: 'web01.example.com',
      macAddress: 'AA:BB:CC:DD:EE:FF',
      isVirtualMachine: true,
      cpuCores: 8,
      memoryMiB: 16384,
      tags: [{ name: 'os', value: 'Debian 12' }],
    })
  })

  it('proposes a service for each port whose type is known, and a credential for each new account', () => {
    const h = host({ services: [service({ type: 'ssh' })], credentials: [credential({ username: 'root' })] })
    const proposals = scanProposals(
      h,
      {
        ports: [
          { port: 22, name: 'OpenSSH' },
          // Two types default to 80, so it's not known; the scanner's own type wins.
          { port: 80 },
          { port: 8080, serviceType: 'http' },
          { port: 8080, serviceType: 'http' },
          // A type with settings a scan can't fill in, and one no plugin provides.
          { port: 5432 },
          { port: 9999, serviceType: 'nope' },
          { port: 5900, name: 'VNC' },
        ],
        users: [{ username: 'ROOT' }, { username: 'ops', fullName: 'Operations' }],
      },
      types,
      [h],
      false,
    )
    expect(proposals.map((p) => [p.group, p.label, p.detail])).toEqual([
      ['Services', 'HTTP', 'port 8080'],
      ['Services', 'VNC', 'port 5900'],
      ['Logins', 'ops', 'Operations · a credential with no password yet'],
    ])

    const draft = draftOf(h)
    proposals.forEach((p) => p.apply?.(draft))
    const body = requestOf(draft, 1)
    expect(body.services.map((s) => [s.type, s.port])).toEqual([
      ['ssh', null],
      ['http', 8080],
      // At its type's default port, the port is left to the default.
      ['vnc', null],
    ])
    expect(body.credentials.map((c) => [c.username, c.serviceIndexes])).toEqual([
      ['root', null],
      ['ops', null],
    ])
  })

  it('starts many accounts unticked, and offers unknown neighbours as new hosts to administrators only', () => {
    const h = host()
    const other = host({ id: 'h2', name: 'db01', ipAddress: '192.0.2.20', macAddress: 'AA:00:00:00:00:02' })
    const findings = {
      users: Array.from({ length: 11 }, (_, i) => ({ username: `user${i}` })),
      neighbors: [{ ipAddress: '192.0.2.20' }, { ipAddress: '192.0.2.21', macAddress: 'aa:00:00:00:00:02' }, { ipAddress: '192.0.2.30', macAddress: 'aa:00:00:00:00:03' }],
    }
    const proposals = scanProposals(h, findings, types, [h, other], true)
    expect(proposals.filter((p) => p.group === 'Logins').every((p) => !p.checked)).toBe(true)
    expect(proposals.filter((p) => p.newHost).map((p) => [p.newHost, p.checked])).toEqual([
      [{ name: '192.0.2.30', ipAddress: '192.0.2.30', macAddress: 'AA:00:00:00:00:03', description: 'Seen in web01’s neighbour table.' }, false],
    ])
    expect(scanProposals(h, findings, types, [h, other], false).some((p) => p.newHost)).toBe(false)
  })
})
