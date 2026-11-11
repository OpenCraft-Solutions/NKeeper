import { afterEach, describe, expect, it } from 'vitest'
import { applyColorScheme, COLOR_SCHEME_PRESETS, contrast, deriveTokens, tokensFor, type ColorSchemeColors, type ColorSchemeTokens } from './colorSchemes'

const TEXT_TOKENS = ['--color-text-muted', '--color-text-subtle', '--color-accent-text', '--color-danger', '--color-success', '--color-warning'] as const

function expectReadable(tokens: ColorSchemeTokens, minimum: number) {
  for (const background of [tokens['--color-bg']!, tokens['--color-surface']!, tokens['--color-surface-raised']!]) {
    for (const name of TEXT_TOKENS) {
      expect(contrast(tokens[name]!, background), `${name} on ${background}`).toBeGreaterThanOrEqual(minimum)
    }
  }
}

describe('color schemes', () => {
  afterEach(() => {
    document.documentElement.removeAttribute('style')
    localStorage.clear()
  })

  it.each(COLOR_SCHEME_PRESETS.map((preset) => [preset.name, preset] as const))('%s is readable everywhere', (_name, preset) => {
    expect(contrast(preset.text, preset.background)).toBeGreaterThanOrEqual(7)
    expect(contrast(preset.text, preset.surface)).toBeGreaterThanOrEqual(7)
    const tokens = deriveTokens(preset)
    expectReadable(tokens, 4.5)
    expect(contrast(tokens['--color-on-accent']!, preset.accent)).toBeGreaterThanOrEqual(4.5)
  })

  it('offers plenty of both dark and light presets, each with its own id, name and colors', () => {
    const modes = COLOR_SCHEME_PRESETS.map((preset) => deriveTokens(preset)['color-scheme'])

    expect(COLOR_SCHEME_PRESETS).toHaveLength(32)
    expect(modes.filter((mode) => mode === 'dark')).toHaveLength(18)
    expect(modes.filter((mode) => mode === 'light')).toHaveLength(14)
    expect(new Set(COLOR_SCHEME_PRESETS.map((preset) => preset.id)).size).toBe(32)
    expect(new Set(COLOR_SCHEME_PRESETS.map((preset) => preset.name)).size).toBe(32)
    expect(new Set(COLOR_SCHEME_PRESETS.map(({ background, surface, text, accent }) => [background, surface, text, accent].join())).size).toBe(32)
  })

  it('keeps derived text readable with awkward custom colors', () => {
    const colors: ColorSchemeColors[] = [
      { background: '#ffffff', surface: '#ffffff', text: '#000000', accent: '#ffff00' },
      { background: '#000000', surface: '#000000', text: '#ffffff', accent: '#000080' },
      { background: '#ff00ff', surface: '#ff00ff', text: '#000000', accent: '#ff00ff' },
    ]
    for (const scheme of colors) {
      expectReadable(deriveTokens(scheme), 4.5)
    }
  })

  it('picks button text that reads on the accent', () => {
    expect(deriveTokens({ background: '#000000', surface: '#111111', text: '#ffffff', accent: '#ffd400' })['--color-on-accent']).toBe('#000000')
    expect(deriveTokens({ background: '#000000', surface: '#111111', text: '#ffffff', accent: '#2563eb' })['--color-on-accent']).toBe('#ffffff')
  })

  it('leaves the default scheme to the stylesheet, also for an id it does not know', () => {
    expect(tokensFor({ active: 'navy', custom: [] })).toEqual({})
    expect(tokensFor({ active: 'deleted-scheme', custom: [] })).toEqual({})
  })

  it('applies and clears a scheme on the root element, and remembers it for the next load', () => {
    const tokens = tokensFor({ active: 'light', custom: [] })
    applyColorScheme(tokens)
    const root = document.documentElement
    expect(root.style.getPropertyValue('--color-bg')).toBe('#f3f5f9')
    expect(root.style.getPropertyValue('color-scheme')).toBe('light')
    expect(JSON.parse(localStorage.getItem('nkeeper.colorSchemeTokens')!)).toEqual(tokens)

    applyColorScheme({})
    expect(root.style.getPropertyValue('--color-bg')).toBe('')
    expect(root.style.getPropertyValue('color-scheme')).toBe('')
  })
})
