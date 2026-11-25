import type { CSSProperties, KeyboardEvent } from 'react'
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

/** How a click picks hosts (1.x): one, one more or less (Ctrl/⌘), or every one from the last (Shift). */
export type SelectMode = 'single' | 'toggle' | 'range'

/**
 * The host tree (docs/architecture.md §7.14), as 1.x drew it: groups that fold, and per host its health, name,
 * address and first service. Selecting, double-clicking and right-clicking are the workspace's; so is picking several
 * hosts to change together (§7.50), with Ctrl/⌘+click, Shift+click, Ctrl/⌘+A and Escape.
 */
export function HostTree({
  groups,
  grouped,
  collapsed,
  onToggle,
  health,
  catalog,
  selectedId,
  picked,
  menuHostId,
  onSelect,
  onSelectAll,
  onClearPicked,
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
  /** The hosts picked to change together; a single host is a selection, not this. */
  picked: Set<string>
  /** The host whose menu is open, highlighted while it is. */
  menuHostId: string | null
  onSelect: (host: Host, mode: SelectMode) => void
  onSelectAll: () => void
  onClearPicked: () => void
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
          className={`${styles.host} ${host.id === menuHostId ? styles.menuOpen : ''} ${picked.has(host.id) ? styles.bulk : ''}`}
          style={{ '--depth': depth } as CSSProperties}
          aria-label={[host.name, hostAddress(host), summary === 'unknown' ? null : HEALTH_LABELS[summary], picked.has(host.id) ? 'selected' : null].filter(Boolean).join(', ')}
          aria-current={host.id === selectedId && picked.size === 0 ? 'true' : undefined}
          aria-haspopup="menu"
          // Keep Shift+click from selecting the page's text.
          onMouseDown={(event) => event.shiftKey && event.preventDefault()}
          onClick={(event) => onSelect(host, event.shiftKey ? 'range' : event.ctrlKey || event.metaKey ? 'toggle' : 'single')}
          onDoubleClick={() => onActivate(host)}
          onContextMenu={(event) => {
            event.preventDefault()
            onContextMenu(host, event.clientX, event.clientY)
          }}
        >
          {picked.has(host.id) && (
            <svg className={styles.check} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
              <path d="M2.5 6.5 5 9l4.5-6" fill="none" stroke="currentColor" strokeWidth="1.8" strokeLinecap="round" strokeLinejoin="round" />
            </svg>
          )}
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
    <ul className={styles.tree} aria-label="Hosts" onKeyDown={(event: KeyboardEvent) => keys(event, onSelectAll, onClearPicked)}>
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

/** 1.x's keys for picking hosts: Ctrl/⌘+A picks every host shown, Escape none. */
function keys(event: KeyboardEvent, onSelectAll: () => void, onClearPicked: () => void) {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'a') {
    event.preventDefault()
    onSelectAll()
  } else if (event.key === 'Escape') {
    onClearPicked()
  }
}
