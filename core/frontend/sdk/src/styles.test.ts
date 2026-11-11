import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { adoptStyles, mirrorInjectedStyles } from './styles'

/** jsdom has no constructable stylesheets, so a stand-in records their text. */
class FakeSheet {
  css = ''
  replaceSync(css: string) {
    this.css = css
  }
}

const adopted = () => (document.adoptedStyleSheets as unknown as FakeSheet[]).map((s) => s.css)
const flush = () => new Promise((resolve) => setTimeout(resolve, 0))

describe('styles', () => {
  beforeEach(() => {
    vi.stubGlobal('CSSStyleSheet', FakeSheet)
    Object.defineProperty(document, 'adoptedStyleSheets', { value: [], writable: true, configurable: true })
  })

  afterEach(() => {
    Reflect.deleteProperty(document, 'adoptedStyleSheets')
  })

  it('adopts a stylesheet from text, and removes it', () => {
    const remove = adoptStyles('.a { color: red }')
    expect(adopted()).toEqual(['.a { color: red }'])
    remove()
    expect(adopted()).toEqual([])
  })

  it('mirrors style elements a library adds, changes and removes', async () => {
    const root = document.createElement('div')
    document.body.append(root)
    const stop = mirrorInjectedStyles(root)

    const style = document.createElement('style')
    style.textContent = '.theme { color: red }'
    root.append(style)
    await flush()
    expect(adopted()).toEqual(['.theme { color: red }'])

    style.textContent = '.theme { color: blue }'
    await flush()
    expect(adopted()).toEqual(['.theme { color: blue }'])

    const other = adoptStyles('.other {}')
    style.remove()
    await flush()
    expect(adopted()).toEqual(['.other {}'])

    root.append(style)
    await flush()
    stop()
    expect(adopted()).toEqual(['.other {}'])
    other()
    root.remove()
  })
})
