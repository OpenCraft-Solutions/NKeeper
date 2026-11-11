import { useCallback, useEffect, useRef, useState } from 'react'
import type { ConnectEntry, ConnectGroup } from './catalog'

/** A host's Connect menu (docs/architecture.md §7.12): a button that opens the entries the plugins' actions offer. */
export function ConnectMenu({
  groups,
  onChoose,
  label = 'Connect',
  name = label,
  open: openProp,
  onOpenChange,
}: {
  groups: ConnectGroup[]
  onChoose: (group: ConnectGroup, entry: ConnectEntry) => void
  /** What the button says. */
  label?: string
  /** Its accessible name, when the label alone isn't enough (several hosts' buttons in one list). */
  name?: string
  /** For a menu opened from elsewhere too (a right-click on the host's row); uncontrolled when left out. */
  open?: boolean
  onOpenChange?: (open: boolean) => void
}) {
  const [openState, setOpenState] = useState(false)
  const open = openProp ?? openState
  const setOpen = useCallback(
    (next: boolean | ((current: boolean) => boolean)) => {
      const value = typeof next === 'function' ? next(open) : next
      setOpenState(value)
      onOpenChange?.(value)
    },
    [open, onOpenChange],
  )
  const root = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) {
      return
    }
    const close = (event: MouseEvent | KeyboardEvent) => {
      if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) {
        setOpen(false)
      }
    }
    document.addEventListener('mousedown', close)
    document.addEventListener('keydown', close)
    return () => {
      document.removeEventListener('mousedown', close)
      document.removeEventListener('keydown', close)
    }
  }, [open, setOpen])

  if (groups.length === 0) {
    return null
  }
  return (
    <div className="nk-menu" ref={root}>
      <button type="button" aria-label={name} aria-haspopup="menu" aria-expanded={open} onClick={() => setOpen((o) => !o)}>
        {label}
      </button>
      {open && (
        <div className="nk-menu-list" role="menu" aria-label={name}>
          {groups.map((group) => (
            <div key={group.key} role="group" aria-label={group.label}>
              <div className="nk-menu-heading">{group.label}</div>
              {group.entries.map((entry) => (
                <button
                  key={entry.key}
                  type="button"
                  role="menuitem"
                  onClick={() => {
                    setOpen(false)
                    onChoose(group, entry)
                  }}
                >
                  {entry.label}
                </button>
              ))}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
