import { describe, expect, it } from 'vitest'
import { findDuplicates, sameMachine } from './duplicates'
import { host as fixture, service } from './test/fixtures'
import type { Host } from './types'

const host = (id: string, name: string, extra: Partial<Host> = {}): Host => fixture({ id, name, hostname: null, ipAddress: null, ...extra })

describe('finding duplicate hosts (1.x)', () => {
  it('decides by the first field both have: MAC, then IP, then hostname, then name', () => {
    // MAC addresses match whatever their separators.
    expect(sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55' }), host('b', 'other', { macAddress: '00-11-22-33-44-55' }))?.label).toBe('MAC address')
    // Different MAC addresses are different machines, whatever else matches.
    expect(sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55', ipAddress: '10.0.0.1' }), host('b', 'web1', { macAddress: 'aa:bb:cc:dd:ee:ff', ipAddress: '10.0.0.1' }))).toBeNull()
    expect(sameMachine(host('a', 'web1', { macAddress: '00:11:22:33:44:55', ipAddress: '10.0.0.1' }), host('b', 'x', { ipAddress: '10.0.0.1' }))?.label).toBe('IP address')
    expect(sameMachine(host('a', 'one', { hostname: 'sql01.corp.lan' }), host('b', 'two', { hostname: 'SQL01' }))?.label).toBe('hostname')
    expect(sameMachine(host('a', 'web1', { ipAddress: '10.0.0.1' }), host('b', 'web1', { ipAddress: '10.0.0.2' }))).toBeNull()
    // Behind different jump hosts, a private address can repeat: only a MAC address counts.
    expect(sameMachine(host('a', 'x', { ipAddress: '192.168.1.10', jump: { hostId: 'j1', serviceId: 's', credentialId: 'c' } }), host('b', 'y', { ipAddress: '192.168.1.10' }))).toBeNull()
    expect(sameMachine(host('a', 'Web1'), host('b', 'web1'))?.label).toBe('name')
  })

  it('groups them transitively, the host holding the most first', () => {
    const groups = findDuplicates([
      host('a', 'app01', { ipAddress: '10.0.0.5' }),
      host('b', 'APP01 old', { ipAddress: '10.0.0.5', macAddress: '00:11:22:33:44:55', services: [service()] }),
      host('c', 'app01-x', { macAddress: '00:11:22:33:44:55' }),
      host('d', 'unique', { ipAddress: '10.0.0.9' }),
    ])

    expect(groups).toHaveLength(1)
    expect(groups[0]!.hosts.map((h) => h.id)).toEqual(['b', 'a', 'c'])
    expect(groups[0]!.reasons).toEqual(expect.arrayContaining(['Same IP address 10.0.0.5', 'Same MAC address 00:11:22:33:44:55']))
  })
})
