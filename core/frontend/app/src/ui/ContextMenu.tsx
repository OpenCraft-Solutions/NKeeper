import { useEffect, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import styles from './ContextMenu.module.css'

export type MenuEntry =
  | {
      kind: 'item'
      label: string
      /** Secondary text, e.g. a port or an account name. */
      detail?: string
      /** The keyboard shortcut that does the same, e.g. "Ctrl+E". */
      shortcut?: string
      disabled?: boolean
      onSelect?: () => void
      submenu?: MenuEntry[]
    }
  | { kind: 'separator' }
  | { kind: 'header'; label: string }

interface ContextMenuProps {
  /** Accessible name of the menu, e.g. "Actions for sql01". */
  label: string
  entries: MenuEntry[]
  /** Viewport coordinates of the pointer (or of the element the menu belongs to). */
  x: number
  y: number
  onClose: () => void
}

/**
 * A context menu with nested submenus, following the ARIA menu pattern:
 * arrows move, Right/Enter open a submenu, Left/Escape go back, typing jumps to an item.
 * Closes on selection, outside click, scroll, resize or window blur, and gives focus back.
 */
export function ContextMenu({ label, entries, x, y, onClose }: ContextMenuProps) {
  const onCloseRef = useRef(onClose)
  useEffect(() => {
    onCloseRef.current = onClose
  })
  // Captured on the first render: by the time effects run, the menu has already taken focus.
  const [opener] = useState(() => (document.activeElement instanceof HTMLElement ? document.activeElement : null))

  useEffect(() => {
    const close = () => onCloseRef.current()
    const closeOnOutsidePointer = (event: PointerEvent) => {
      if (!(event.target instanceof Element) || !event.target.closest('[data-context-menu]')) {
        close()
      }
    }
    document.addEventListener('pointerdown', closeOnOutsidePointer, true)
    window.addEventListener('resize', close)
    window.addEventListener('blur', close)
    document.addEventListener('scroll', close, true)
    return () => {
      document.removeEventListener('pointerdown', closeOnOutsidePointer, true)
      window.removeEventListener('resize', close)
      window.removeEventListener('blur', close)
      document.removeEventListener('scroll', close, true)
      // Return focus to the element the menu was opened from, if it still exists.
      if (opener?.isConnected) {
        opener.focus()
      }
    }
  }, [opener])

  return createPortal(
    <MenuList label={label} entries={entries} anchor={{ left: x, top: y, right: x }} onCloseAll={onClose} onBack={onClose} level={0} />,
    document.body,
  )
}

interface Anchor {
  left: number
  top: number
  /** Right edge of the parent item, where a submenu opens. */
  right: number
}

interface MenuListProps {
  label: string
  entries: MenuEntry[]
  anchor: Anchor
  onCloseAll: () => void
  /** Closes this level only (Left/Escape in a submenu), returning focus to its parent item. */
  onBack: () => void
  level: number
}

const VIEWPORT_MARGIN = 8

function MenuList({ label, entries, anchor, onCloseAll, onBack, level }: MenuListProps) {
  const listRef = useRef<HTMLUListElement>(null)
  const itemRefs = useRef<(HTMLButtonElement | null)[]>([])
  const [openIndex, setOpenIndex] = useState<number | null>(null)
  const [submenuAnchor, setSubmenuAnchor] = useState<Anchor | null>(null)

  const focusable = entries.flatMap((entry, index) => (entry.kind === 'item' ? [index] : []))

  // Place the menu at the anchor, flipped or clamped to stay inside the viewport, then focus it.
  useLayoutEffect(() => {
    const list = listRef.current
    if (!list) {
      return
    }
    const { width, height } = list.getBoundingClientRect()
    let left = level === 0 ? anchor.left : anchor.right - 4
    if (left + width > window.innerWidth - VIEWPORT_MARGIN) {
      left = level === 0 ? window.innerWidth - VIEWPORT_MARGIN - width : anchor.left - width + 4
    }
    const top = Math.min(anchor.top, window.innerHeight - VIEWPORT_MARGIN - height)
    list.style.left = `${Math.max(VIEWPORT_MARGIN, left)}px`
    list.style.top = `${Math.max(VIEWPORT_MARGIN, top)}px`
    itemRefs.current[focusable[0] ?? -1]?.focus()
    // Only on mount: the anchor does not move while the menu is open.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  function openSubmenu(index: number) {
    const rect = itemRefs.current[index]?.getBoundingClientRect()
    if (rect) {
      setSubmenuAnchor({ left: rect.left, top: rect.top - 5, right: rect.right })
      setOpenIndex(index)
    }
  }

  function closeSubmenu() {
    const index = openIndex
    setOpenIndex(null)
    if (index !== null) {
      itemRefs.current[index]?.focus()
    }
  }

  function activate(index: number) {
    const entry = entries[index]
    if (entry?.kind !== 'item' || entry.disabled) {
      return
    }
    if (entry.submenu) {
      openSubmenu(index)
    } else {
      onCloseAll()
      entry.onSelect?.()
    }
  }

  function moveFocus(from: number, step: 1 | -1) {
    const position = focusable.indexOf(from)
    const next = focusable[(position + step + focusable.length) % focusable.length]
    itemRefs.current[next ?? -1]?.focus()
  }

  function handleKeyDown(event: KeyboardEvent<HTMLButtonElement>, index: number) {
    const entry = entries[index]
    const handled = () => {
      event.preventDefault()
      event.stopPropagation()
    }
    switch (event.key) {
      case 'ArrowDown':
        handled()
        moveFocus(index, 1)
        break
      case 'ArrowUp':
        handled()
        moveFocus(index, -1)
        break
      case 'Home':
        handled()
        itemRefs.current[focusable[0] ?? -1]?.focus()
        break
      case 'End':
        handled()
        itemRefs.current[focusable[focusable.length - 1] ?? -1]?.focus()
        break
      case 'ArrowRight':
        if (entry?.kind === 'item' && entry.submenu && !entry.disabled) {
          handled()
          openSubmenu(index)
        }
        break
      case 'Enter':
      case ' ':
        handled()
        activate(index)
        break
      case 'ArrowLeft':
        if (level > 0) {
          handled()
          onBack()
        }
        break
      case 'Escape':
        handled()
        onBack()
        break
      case 'Tab':
        handled()
        onCloseAll()
        break
      default:
        if (event.key.length === 1 && /\S/.test(event.key)) {
          // Typeahead: jump to the next item starting with the typed character.
          const key = event.key.toLowerCase()
          const start = focusable.indexOf(index)
          const ordered = [...focusable.slice(start + 1), ...focusable.slice(0, start + 1)]
          const match = ordered.find((i) => {
            const candidate = entries[i]
            return candidate?.kind === 'item' && candidate.label.toLowerCase().startsWith(key)
          })
          if (match !== undefined) {
            handled()
            itemRefs.current[match]?.focus()
          }
        }
    }
  }

  const openEntry = openIndex !== null ? entries[openIndex] : null

  return (
    <>
      <ul ref={listRef} className={styles.menu} role="menu" aria-label={label} data-context-menu onContextMenu={(e) => e.preventDefault()}>
        {entries.map((entry, index) => {
          if (entry.kind === 'separator') {
            return <li key={index} role="separator" className={styles.separator} />
          }
          if (entry.kind === 'header') {
            return (
              <li key={index} role="presentation" className={styles.header}>
                {entry.label}
              </li>
            )
          }
          const hasSubmenu = Boolean(entry.submenu)
          return (
            <li key={index} role="none">
              <button
                ref={(element) => {
                  itemRefs.current[index] = element
                }}
                type="button"
                role="menuitem"
                tabIndex={-1}
                className={`${styles.item} ${openIndex === index ? styles.open : ''}`}
                aria-disabled={entry.disabled || undefined}
                aria-haspopup={hasSubmenu ? 'menu' : undefined}
                aria-expanded={hasSubmenu ? openIndex === index : undefined}
                aria-keyshortcuts={entry.shortcut}
                onClick={() => activate(index)}
                onKeyDown={(event) => handleKeyDown(event, index)}
                onPointerEnter={() => {
                  itemRefs.current[index]?.focus()
                  if (hasSubmenu && !entry.disabled) {
                    openSubmenu(index)
                  } else if (openIndex !== null) {
                    setOpenIndex(null)
                  }
                }}
              >
                <span className={styles.label}>{entry.label}</span>
                {entry.detail ? <span className={styles.detail}>{entry.detail}</span> : null}
                {entry.shortcut ? <kbd className={styles.shortcut}>{entry.shortcut}</kbd> : null}
                {hasSubmenu ? (
                  <svg className={styles.chevron} viewBox="0 0 12 12" width="12" height="12" aria-hidden="true">
                    <path d="M4.5 2.5 8 6l-3.5 3.5" fill="none" stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" />
                  </svg>
                ) : null}
              </button>
            </li>
          )
        })}
      </ul>
      {openEntry?.kind === 'item' && openEntry.submenu && submenuAnchor ? (
        <MenuList
          key={openIndex}
          label={openEntry.label}
          entries={openEntry.submenu}
          anchor={submenuAnchor}
          onCloseAll={onCloseAll}
          onBack={closeSubmenu}
          level={level + 1}
        />
      ) : null}
    </>
  )
}
