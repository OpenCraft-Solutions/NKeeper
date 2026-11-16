import type { CSSProperties } from 'react'
import type { Catalog } from './catalog'
import { healthSummary, hostAddress, type HealthSummary, type HostGroup } from './hosts'
import type { Host, ServiceHealth } from './types'
import styles from './ui/HostTree.module.css'

/** 1.x's labels for a host's health dot; a host nothing checked has none. */
const HEALTH_LABELS: Record<Exclude<HealthSummary, 'unknown'>, string> = {
  up: 'All services up',
  partial: 'Some services down',
  down: 'Not answering',
}

/**
 * The host tree (docs/architecture.md §7.14), as 1.x drew it: groups that fold, and per host its health, name,
 * address and first service. Selecting, double-clicking and right-clicking are the workspace's.
 */
export function HostTree({
  groups,
  grouped,
  collapsed,
  onToggle,
  health,
  catalog,
  selectedId,
  menuHostId,
  onSelect,
  onActivate,
  onContextMenu,
}: {
  groups: HostGroup[]
  /** Whether the hosts are grouped: without grouping, the one group has no row of its own. */
  grouped: boolean
  collapsed: Set<string>
  onToggle: (key: string) => void
  health: Map<string, ServiceHealth>
  catalog: Catalog
  selectedId: string | null
  /** The host whose menu is open, highlighted while it is. */
  menuHostId: string | null
  onSelect: (host: Host) => void
  onActivate: (host: Host) => void
  onContextMenu: (host: Host, x: number, y: number) => void
}) {
  const hostRow = (host: Host, depth: number) => {
    const summary = healthSummary(host, health)
    const first = host.services[0]
    return (
      <li key={host.id}>
        <button
          type="button"
          data-host-id={host.id}
          className={`${styles.host} ${host.id === menuHostId ? styles.menuOpen : ''}`}
          style={{ '--depth': depth } as CSSProperties}
          aria-label={[host.name, hostAddress(host), summary === 'unknown' ? null : HEALTH_LABELS[summary]].filter(Boolean).join(', ')}
          aria-current={host.id === selectedId ? 'true' : undefined}
          aria-haspopup="menu"
          onClick={() => onSelect(host)}
          onDoubleClick={() => onActivate(host)}
          onContextMenu={(event) => {
            event.preventDefault()
            onContextMenu(host, event.clientX, event.clientY)
          }}
        >
          {summary !== 'unknown' && (
            <span className={`${styles.health} ${styles[summary]}`} title={HEALTH_LABELS[summary]} role="img" aria-label={HEALTH_LABELS[summary]} />
          )}
          <span className={styles.hostText}>
            <span className={styles.hostName}>{host.name}</span>
            <span className={styles.hostAddress}>{hostAddress(host)}</span>
          </span>
          {first && (
            <span className={styles.service} title={host.services.map((s) => catalog.serviceTypes.get(s.type)?.label ?? s.type).join(', ')}>
              {catalog.serviceTypes.get(first.type)?.label ?? first.type}
              {host.services.length > 1 ? ` +${host.services.length - 1}` : null}
            </span>
          )}
        </button>
      </li>
    )
  }

  return (
    <ul className={styles.tree} aria-label="Hosts">
      {grouped
        ? groups.map((group) => {
            const expanded = !collapsed.has(group.key)
            return (
              <li key={group.key}>
                <button type="button" className={styles.group} aria-expanded={expanded} onClick={() => onToggle(group.key)}>
                  <svg className={styles.chevron} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
                    <path d="M4.5 2.5 8 6l-3.5 3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" />
                  </svg>
                  <span className={styles.groupLabel}>{group.label}</span>
                  <span className={styles.count}>{group.hosts.length}</span>
                </button>
                {expanded && <ul>{group.hosts.map((host) => hostRow(host, 1))}</ul>}
              </li>
            )
          })
        : groups.flatMap((group) => group.hosts.map((host) => hostRow(host, 0)))}
    </ul>
  )
}
