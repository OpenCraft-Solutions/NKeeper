/**
 * Styles under the shell's Content Security Policy (docs/architecture.md §7.14). The policy allows
 * stylesheets only from the server (`style-src 'self'`), so a `<style>` element added at run time is
 * blocked. Constructable stylesheets are allowed, so these helpers turn text into those instead.
 */

function supported(): boolean {
  return typeof CSSStyleSheet !== 'undefined' && typeof document !== 'undefined' && 'adoptedStyleSheets' in document
}

function sheetFrom(css: string): CSSStyleSheet {
  const sheet = new CSSStyleSheet()
  sheet.replaceSync(css)
  return sheet
}

/** Adds a stylesheet from text, e.g. a library's CSS imported with `?inline`. Returns a function that removes it. */
export function adoptStyles(css: string): () => void {
  if (!supported()) {
    return () => {}
  }
  const sheet = sheetFrom(css)
  document.adoptedStyleSheets = [...document.adoptedStyleSheets, sheet]
  return () => {
    document.adoptedStyleSheets = document.adoptedStyleSheets.filter((s) => s !== sheet)
  }
}

/**
 * Copies the `<style>` elements a library adds under `root` (xterm.js does, for its theme and sizes) into
 * constructable stylesheets, and keeps them in step as the library changes or removes them. Returns a
 * function that stops, and removes the copies.
 */
export function mirrorInjectedStyles(root: Element): () => void {
  if (!supported()) {
    return () => {}
  }
  const copies = new Map<HTMLStyleElement, { css: string; sheet: CSSStyleSheet }>()

  const sync = () => {
    const present = new Set(root.querySelectorAll('style'))
    let changed = false
    for (const [element] of copies) {
      if (!present.has(element)) {
        copies.delete(element)
        changed = true
      }
    }
    for (const element of present) {
      const css = element.textContent ?? ''
      const copy = copies.get(element)
      if (!copy) {
        copies.set(element, { css, sheet: sheetFrom(css) })
        changed = true
      } else if (copy.css !== css) {
        copy.sheet.replaceSync(css)
        copy.css = css
      }
    }
    if (changed) {
      const ours = new Set([...copies.values()].map((c) => c.sheet))
      document.adoptedStyleSheets = [...document.adoptedStyleSheets.filter((s) => !ownSheets.has(s)), ...ours]
      ownSheets = ours
    }
  }

  let ownSheets = new Set<CSSStyleSheet>()
  const observer = new MutationObserver(sync)
  observer.observe(root, { childList: true, subtree: true, characterData: true })
  sync()
  return () => {
    observer.disconnect()
    document.adoptedStyleSheets = document.adoptedStyleSheets.filter((s) => !ownSheets.has(s))
    ownSheets = new Set()
    copies.clear()
  }
}
