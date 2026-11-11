import { useMemo, useState } from 'react'
import { connectMenu, type Catalog, type ConnectEntry, type ConnectGroup } from './catalog'
import { ConnectMenu } from './ConnectMenu'
import { groupHosts, healthSummary, hostAddress, matchesSearch, tagNames, type HealthSummary } from './hosts'
import type { Host, ServiceHealth } from './types'

const GROUP_BY_KEY = 'nkeeper.groupBy'

const HEALTH_LABELS: Record<HealthSummary, string> = {
  up: 'Every checked service is up',
  down: 'Every checked service is down',
  partial: 'Some checked services are down',
  unknown: 'Not checked',
}

/** The choice is kept in this browser only; storage that's blocked just means no memory of it. */
function readGroupBy(): string {
  try {
    return localStorage.getItem(GROUP_BY_KEY) ?? ''
  } catch {
    return ''
  }
}

function writeGroupBy(value: string) {
  try {
    localStorage.setItem(GROUP_BY_KEY, value)
  } catch {
    // Not kept.
  }
}

export function HealthDot({ summary }: { summary: HealthSummary }) {
  return <span className={`nk-dot nk-dot-${summary}`} role="img" aria-label={HEALTH_LABELS[summary]} title={HEALTH_LABELS[summary]} />
}

/** The host tree (docs/architecture.md §7.14): search, group by a tag, health, and each host's Connect menu. */
export function HostTree({
  hosts,
  health,
  catalog,
  selectedId,
  onSelect,
  onConnect,
}: {
  hosts: Host[]
  health: Map<string, ServiceHealth>
  catalog: Catalog
  selectedId: string | null
  onSelect: (host: Host) => void
  onConnect: (host: Host, group: ConnectGroup, entry: ConnectEntry) => void
}) {
  const [query, setQuery] = useState('')
  const [groupBy, setGroupBy] = useState(readGroupBy)
  const names = useMemo(() => tagNames(hosts), [hosts])
  const effectiveGroupBy = names.includes(groupBy) ? groupBy : ''
  const groups = useMemo(() => groupHosts(hosts.filter((h) => matchesSearch(h, query)), effectiveGroupBy || null), [hosts, query, effectiveGroupBy])
  const shown = groups.reduce((n, g) => n + g.hosts.length, 0)

  return (
    <nav className="nk-tree" aria-label="Hosts">
      <div className="nk-tree-tools">
        <input type="search" aria-label="Search hosts" placeholder="Search hosts" value={query} onChange={(e) => setQuery(e.target.value)} />
        <label>
          Group by
          <select
            value={effectiveGroupBy}
            onChange={(e) => {
              setGroupBy(e.target.value)
              writeGroupBy(e.target.value)
            }}
          >
            <option value="">Nothing</option>
            {names.map((name) => (
              <option key={name} value={name}>
                {name}
              </option>
            ))}
          </select>
        </label>
      </div>
      {hosts.length === 0 ? (
        <p className="nk-muted nk-pad">No hosts yet.</p>
      ) : shown === 0 ? (
        <p className="nk-muted nk-pad">No host matches.</p>
      ) : (
        groups.map((group) => (
          <section key={group.key} className="nk-tree-group">
            {effectiveGroupBy && (
              <h3>
                {group.label} <span className="nk-muted">({group.hosts.length})</span>
              </h3>
            )}
            <ul>
              {group.hosts.map((host) => (
                <li key={host.id} className={host.id === selectedId ? 'nk-selected' : undefined}>
                  <button
                    type="button"
                    className="nk-tree-host"
                    aria-label={[host.name, hostAddress(host), HEALTH_LABELS[healthSummary(host, health)]].filter(Boolean).join(', ')}
                    aria-current={host.id === selectedId ? 'true' : undefined}
                    onClick={() => onSelect(host)}
                  >
                    <HealthDot summary={healthSummary(host, health)} />
                    <span className="nk-tree-name">{host.name}</span>
                    <span className="nk-tree-address">{hostAddress(host)}</span>
                  </button>
                  <ConnectMenu label={`Connect to ${host.name}`} groups={connectMenu(host, catalog)} onChoose={(g, e) => onConnect(host, g, e)} />
                </li>
              ))}
            </ul>
          </section>
        ))
      )}
    </nav>
  )
}
