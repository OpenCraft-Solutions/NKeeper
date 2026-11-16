import { describe, expect, it } from 'vitest'
import { buildCatalog, connectMenu } from './catalog'
import { groupHosts, healthSummary, matchesSearch, tagNames } from './hosts'
import { credential, host, service, terminalPlugin } from './test/fixtures'
import { definePlugin } from '@nkeeper/sdk'

const web = host({ id: 'h1', name: 'web01', tags: [{ name: 'env', value: 'prod' }, { name: 'role', value: 'web' }] })
const db = host({ id: 'h2', name: 'db01', ipAddress: '192.0.2.20', hostname: null, description: 'The main database', tags: [{ name: 'env', value: 'test' }] })
const bare = host({ id: 'h3', name: 'spare', ipAddress: null, hostname: 'spare.example.com', tags: [{ name: 'pinned', value: null }] })

describe('search', () => {
  it.each([
    ['', ['web01', 'db01', 'spare']],
    ['WEB', ['web01']],
    ['192.0.2.20', ['db01']],
    ['database', ['db01']],
    ['env=prod', ['web01']],
    ['env', ['web01', 'db01']],
    ['pinned', ['spare']],
    ['env=test db', ['db01']],
    ['env=prod db', []],
  ])('“%s” matches %j', (query, names) => {
    expect([web, db, bare].filter((h) => matchesSearch(h, query)).map((h) => h.name)).toEqual(names)
  })
})

describe('grouping', () => {
  it('groups by a tag’s values, then the hosts without it', () => {
    const groups = groupHosts([web, db, bare], 'env')
    expect(groups.map((g) => [g.label, g.hosts.map((h) => h.name)])).toEqual([
      ['prod', ['web01']],
      ['test', ['db01']],
      ['No env', ['spare']],
    ])
  })

  it('puts a host with two values in both groups, and lists every tag name', () => {
    const twice = host({ id: 'h4', name: 'both', tags: [{ name: 'env', value: 'prod' }, { name: 'env', value: 'test' }] })
    expect(groupHosts([twice], 'env').map((g) => g.label)).toEqual(['prod', 'test'])
    expect(tagNames([web, db, bare])).toEqual(['env', 'pinned', 'role'])
    expect(groupHosts([web, db], null)).toEqual([{ key: '', label: 'All hosts', hosts: [web, db] }])
  })
})

describe('health', () => {
  it('summarizes a host’s checked services', () => {
    const h = host({ services: [service({ id: 'a' }), service({ id: 'b' })] })
    const result = (id: string, state: 'up' | 'down') => [id, { hostId: 'h1', serviceId: id, state, latencyMs: 1, checkedAtUtc: '', reason: null }] as const
    expect(healthSummary(h, new Map())).toBe('unknown')
    expect(healthSummary(h, new Map([result('a', 'up')]))).toBe('up')
    expect(healthSummary(h, new Map([result('a', 'up'), result('b', 'down')]))).toBe('partial')
    expect(healthSummary(h, new Map([result('a', 'down'), result('b', 'down')]))).toBe('down')
  })
})

describe('the Connect menu', () => {
  const View = () => null
  const catalog = buildCatalog(
    [terminalPlugin()],
    new Map([['term', { kind: 'active', definition: definePlugin({ id: 'term', sessionViews: { term: View } }) }]]),
  )

  it('offers each saved credential used with the service, and typing one', () => {
    const h = host({
      services: [service({ id: 's1', port: 2222 }), service({ id: 's2', type: 'other' })],
      credentials: [credential({ id: 'c1', username: 'root' }), credential({ id: 'c2', label: 'Deploy', serviceIndexes: [] }), credential({ id: 'c3', username: 'ops', domain: 'CORP', serviceIndexes: [0] })],
    })
    const [group, reboot, ...rest] = connectMenu(h, catalog)
    expect(rest).toEqual([])
    // The run action, on the same service, with no credential.
    expect(reboot!.label).toBe('Reboot · Term :2222')
    expect(reboot!.entries).toEqual([
      { key: 'term.reboot/s1', label: 'Reboot', target: { kind: 'run', pluginId: 'term', endpoint: 'reboot' }, serviceId: 's1', credential: { kind: 'none' } },
    ])
    expect(group!.label).toBe('Terminal · Term :2222')
    expect(group!.entries.map((e) => e.label)).toEqual(['root', 'CORP\\ops', 'Enter credentials…'])
    expect(group!.entries[0]!.credential).toEqual({ kind: 'saved', id: 'c1', name: 'root' })
  })

  it('offers nothing without a view, for an unavailable type, or past a jump host the type can’t use', () => {
    const h = host({ services: [service()] })
    // Without a view, only the run action is left.
    expect(connectMenu(h, buildCatalog([terminalPlugin()], new Map())).map((g) => g.actionLabel)).toEqual(['Reboot'])
    expect(connectMenu(host({ services: [service({ available: false })] }), catalog)).toEqual([])
    const noJump = buildCatalog(
      [terminalPlugin({ sessionTypes: [{ id: 'term', label: 'Terminal', serviceTypes: ['term'], supportsJumpHost: false }] })],
      new Map([['term', { kind: 'active', definition: definePlugin({ id: 'term', sessionViews: { term: View } }) }]]),
    )
    // The session can't go through a jump host; the run action doesn't need to, since the server runs it.
    expect(connectMenu(host({ services: [service()], jump: { hostId: 'j', serviceId: 's', credentialId: 'c' } }), noJump).map((g) => g.actionLabel)).toEqual(['Reboot'])
  })

  it('takes a plugin’s views only for its own session types', () => {
    const other = buildCatalog(
      [terminalPlugin(), terminalPlugin({ id: 'sneaky', sessionTypes: [], hostActions: [], serviceTypes: [] })],
      new Map([['sneaky', { kind: 'active', definition: definePlugin({ id: 'sneaky', sessionViews: { term: View } }) }]]),
    )
    expect(other.views.size).toBe(0)
  })
})
