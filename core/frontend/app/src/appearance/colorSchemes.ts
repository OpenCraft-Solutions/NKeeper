/** The colors a scheme is made from; every other color of the app is derived from them. */
export interface ColorSchemeColors {
  background: string
  surface: string
  text: string
  accent: string
}

/** A scheme the user built. Mirrors the API's CustomColorScheme. */
export interface CustomColorScheme extends ColorSchemeColors {
  id: string
  name: string
}

export interface ColorSchemePreferences {
  /** The id of a preset or of one of `custom`. */
  active: string
  custom: CustomColorScheme[]
}

export interface ColorSchemePreset extends ColorSchemeColors {
  id: string
  name: string
}

export const DEFAULT_COLOR_SCHEME = 'navy'
export const MAX_CUSTOM_COLOR_SCHEMES = 20
/** Below this, text is hard to read for most people; a custom scheme can't be saved. */
export const MIN_TEXT_CONTRAST = 3
/** WCAG AA for body text; below this, the builder warns. */
export const GOOD_TEXT_CONTRAST = 4.5

/** The default scheme: what the tokens in styles.css already say. */
const NAVY: ColorSchemePreset = { id: DEFAULT_COLOR_SCHEME, name: 'Navy', background: '#0b1220', surface: '#111a2e', text: '#e5ebf5', accent: '#2563eb' }

export const COLOR_SCHEME_PRESETS: readonly ColorSchemePreset[] = [
  NAVY,
  { id: 'light', name: 'Light', background: '#f3f5f9', surface: '#ffffff', text: '#1a2233', accent: '#2563eb' },
  { id: 'sand', name: 'Sand', background: '#f6f1e7', surface: '#fffdf8', text: '#2b2620', accent: '#b4531a' },
  { id: 'graphite', name: 'Graphite', background: '#141518', surface: '#1c1e22', text: '#e8e9ec', accent: '#5b6ee1' },
  { id: 'forest', name: 'Forest', background: '#0f1a14', surface: '#16241c', text: '#e4efe7', accent: '#2f855a' },
  { id: 'plum', name: 'Plum', background: '#171120', surface: '#211830', text: '#ece6f5', accent: '#7c3aed' },
  { id: 'high-contrast', name: 'High contrast', background: '#000000', surface: '#0a0a0a', text: '#ffffff', accent: '#ffd400' },
  // Dark
  { id: 'deep-sea', name: 'Deep Sea', background: '#081a1f', surface: '#0e2830', text: '#dff3f2', accent: '#0d9488' },
  { id: 'ember', name: 'Ember', background: '#1a100c', surface: '#261812', text: '#f5e7df', accent: '#c2410c' },
  { id: 'oxblood', name: 'Oxblood', background: '#1a0c10', surface: '#271419', text: '#f5e3e7', accent: '#be123c' },
  { id: 'mulberry', name: 'Mulberry', background: '#1c0d1c', surface: '#2a152a', text: '#f3e4f3', accent: '#a21caf' },
  { id: 'slate', name: 'Slate', background: '#12171f', surface: '#1b222d', text: '#e6eaf0', accent: '#0284c7' },
  { id: 'moss', name: 'Moss', background: '#14170c', surface: '#1f2413', text: '#eef0dc', accent: '#65803a' },
  { id: 'indigo-night', name: 'Indigo Night', background: '#0e0f26', surface: '#17193a', text: '#e6e7fa', accent: '#4f46e5' },
  { id: 'espresso', name: 'Espresso', background: '#17110d', surface: '#221a14', text: '#efe6dd', accent: '#a16207' },
  { id: 'amber-terminal', name: 'Amber Terminal', background: '#120e05', surface: '#1d1709', text: '#ffe9b8', accent: '#d97706' },
  { id: 'phosphor', name: 'Phosphor', background: '#05120a', surface: '#0a1f12', text: '#c8f7d4', accent: '#16a34a' },
  { id: 'arctic-night', name: 'Arctic Night', background: '#0c1620', surface: '#142230', text: '#e3eef7', accent: '#38bdf8' },
  { id: 'rosewood', name: 'Rosewood', background: '#1b1113', surface: '#28191c', text: '#f3e5e6', accent: '#db2777' },
  { id: 'storm', name: 'Storm', background: '#15191d', surface: '#1f2529', text: '#e4e8eb', accent: '#64748b' },
  // Light
  { id: 'paper', name: 'Paper', background: '#f5f5f0', surface: '#ffffff', text: '#22221e', accent: '#57534e' },
  { id: 'sky', name: 'Sky', background: '#eef6fc', surface: '#ffffff', text: '#13293d', accent: '#0369a1' },
  { id: 'mint', name: 'Mint', background: '#edf8f3', surface: '#ffffff', text: '#12302a', accent: '#0f766e' },
  { id: 'blossom', name: 'Blossom', background: '#fcf0f4', surface: '#fffafb', text: '#3a1a26', accent: '#be185d' },
  { id: 'lavender', name: 'Lavender', background: '#f3f0fb', surface: '#fdfcff', text: '#241a3d', accent: '#6d28d9' },
  { id: 'butter', name: 'Butter', background: '#fbf6dc', surface: '#fffdf0', text: '#2e2a12', accent: '#a16207' },
  { id: 'linen', name: 'Linen', background: '#f4efe9', surface: '#fbf8f4', text: '#2f2822', accent: '#9a3412' },
  { id: 'sage', name: 'Sage', background: '#eef2ea', surface: '#fafcf8', text: '#1f2a1d', accent: '#4d7c0f' },
  { id: 'peach', name: 'Peach', background: '#fdf0e6', surface: '#fffaf5', text: '#3a2416', accent: '#c2410c' },
  { id: 'cloud', name: 'Cloud', background: '#eceff3', surface: '#f9fafb', text: '#1f2933', accent: '#475569' },
  { id: 'seafoam', name: 'Seafoam', background: '#e6f5f4', surface: '#f8fdfc', text: '#0f2e30', accent: '#0e7490' },
  { id: 'coral', name: 'Coral', background: '#fdeeec', surface: '#fffaf9', text: '#3b1b18', accent: '#dc2626' },
]

type Rgb = [number, number, number]

function toRgb(hex: string): Rgb {
  return [1, 3, 5].map((start) => parseInt(hex.slice(start, start + 2), 16)) as Rgb
}

function mix(from: string, to: string, amount: number): string {
  const [a, b] = [toRgb(from), toRgb(to)]
  const channels = a.map((channel, i) => Math.round(channel + ((b[i] ?? channel) - channel) * amount))
  return `#${channels.map((channel) => channel.toString(16).padStart(2, '0')).join('')}`
}

function luminance(hex: string): number {
  const [r, g, b] = toRgb(hex).map((channel) => {
    const value = channel / 255
    return value <= 0.03928 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4
  }) as Rgb
  return 0.2126 * r + 0.7152 * g + 0.0722 * b
}

/** The WCAG contrast ratio of two colors, from 1 (the same) to 21 (black on white). */
export function contrast(a: string, b: string): number {
  const [x, y] = [luminance(a), luminance(b)]
  return (Math.max(x, y) + 0.05) / (Math.min(x, y) + 0.05)
}

const WHITE = '#ffffff'
const BLACK = '#000000'

/** Moves a color toward black or white, whichever helps, until it reads on all the backgrounds. */
function readable(color: string, backgrounds: string[], minimum: number): string {
  const worst = (candidate: string) => Math.min(...backgrounds.map((background) => contrast(candidate, background)))
  const target = worst(WHITE) >= worst(BLACK) ? WHITE : BLACK
  for (let amount = 0; amount < 1; amount += 0.05) {
    const candidate = mix(color, target, amount)
    if (worst(candidate) >= minimum) {
      return candidate
    }
  }
  return target
}

/** Every token of `styles/global.css` a scheme changes, so they can be cleared again. */
const TOKEN_NAMES = [
  'color-scheme',
  '--color-bg',
  '--color-surface',
  '--color-surface-raised',
  '--color-input',
  '--color-border',
  '--color-border-strong',
  '--color-border-hover',
  '--color-text',
  '--color-text-muted',
  '--color-text-subtle',
  '--color-accent',
  '--color-accent-hover',
  '--color-accent-text',
  '--color-accent-text-hover',
  '--color-on-accent',
  '--color-focus',
  '--color-danger',
  '--color-danger-border',
  '--color-danger-bg',
  '--color-success',
  '--color-warning',
  '--color-info-bg',
  '--color-info-border',
  '--shadow-card',
] as const

export type ColorSchemeTokens = Partial<Record<(typeof TOKEN_NAMES)[number], string>>

/** The full palette for a scheme's base colors, kept readable whatever colors were chosen. */
export function deriveTokens({ background, surface, text, accent }: ColorSchemeColors): ColorSchemeTokens {
  const isLight = luminance(background) > 0.4
  const raised = mix(surface, text, 0.05)
  const backgrounds = [background, surface, raised]
  const accentText = readable(accent, backgrounds, GOOD_TEXT_CONTRAST)
  const onAccent = contrast(WHITE, accent) >= GOOD_TEXT_CONTRAST || contrast(WHITE, accent) >= contrast(BLACK, accent) ? WHITE : BLACK
  const danger = readable(isLight ? '#b91c1c' : '#fca5a5', backgrounds, GOOD_TEXT_CONTRAST)

  return {
    'color-scheme': isLight ? 'light' : 'dark',
    '--color-bg': background,
    '--color-surface': surface,
    '--color-surface-raised': raised,
    '--color-input': isLight ? surface : mix(background, surface, 0.3),
    '--color-border': mix(surface, text, 0.14),
    '--color-border-strong': mix(surface, text, 0.24),
    '--color-border-hover': mix(surface, text, 0.36),
    '--color-text': text,
    '--color-text-muted': readable(mix(text, surface, 0.25), backgrounds, GOOD_TEXT_CONTRAST),
    '--color-text-subtle': readable(mix(text, surface, 0.42), backgrounds, GOOD_TEXT_CONTRAST),
    '--color-accent': accent,
    '--color-accent-hover': mix(accent, onAccent === WHITE ? BLACK : WHITE, 0.15),
    '--color-accent-text': accentText,
    '--color-accent-text-hover': mix(accentText, text, 0.4),
    '--color-on-accent': onAccent,
    '--color-focus': readable(accent, backgrounds, MIN_TEXT_CONTRAST),
    '--color-danger': danger,
    '--color-danger-border': mix(danger, surface, 0.35),
    '--color-danger-bg': `${danger}1f`,
    '--color-success': readable(isLight ? '#15803d' : '#86efac', backgrounds, GOOD_TEXT_CONTRAST),
    '--color-warning': readable(isLight ? '#92400e' : '#f5b84b', backgrounds, GOOD_TEXT_CONTRAST),
    '--color-info-bg': `${accent}1f`,
    '--color-info-border': mix(accent, surface, 0.45),
    '--shadow-card': isLight ? '0 1px 2px rgb(15 23 42 / 0.08), 0 8px 24px rgb(15 23 42 / 0.1)' : '0 1px 2px rgb(0 0 0 / 0.3), 0 8px 24px rgb(0 0 0 / 0.25)',
  }
}

/** The active scheme's base colors. An id that isn't known (a deleted scheme, say) means the default. */
export function activeColors({ active, custom }: ColorSchemePreferences): ColorSchemeColors {
  return (
    custom.find((scheme) => scheme.id === active) ??
    COLOR_SCHEME_PRESETS.find((preset) => preset.id === active) ??
    NAVY
  )
}

/** The tokens to apply. The default scheme needs none: it is what `styles/global.css` already says. */
export function tokensFor(preferences: ColorSchemePreferences): ColorSchemeTokens {
  const colors = activeColors(preferences)
  return colors === NAVY ? {} : deriveTokens(colors)
}

/** Read by public/color-scheme.js, which applies the last scheme before the first paint. */
const STORAGE_KEY = 'nkeeper.colorSchemeTokens'

/** Colors the whole app, and remembers them for this browser's next page load. */
export function applyColorScheme(tokens: ColorSchemeTokens) {
  const root = document.documentElement
  for (const name of TOKEN_NAMES) {
    const value = tokens[name]
    if (value) {
      root.style.setProperty(name, value)
    } else {
      root.style.removeProperty(name)
    }
  }
  document.querySelector('meta[name="theme-color"]')?.setAttribute('content', tokens['--color-bg'] ?? NAVY.background)
  document.querySelector('meta[name="color-scheme"]')?.setAttribute('content', tokens['color-scheme'] ?? 'dark')
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(tokens))
  } catch {
    // Storage is blocked: only a brief flash of the default colors on the next load is lost.
  }
}

/** Just the custom properties, to color one element and its contents (the builder's preview). */
export function previewStyle(tokens: ColorSchemeTokens): Record<string, string> {
  return Object.fromEntries(Object.entries(tokens).filter(([name]) => name.startsWith('--')))
}

/** An id for a new custom scheme. `crypto.randomUUID` isn't available over plain HTTP, which NKeeper may be served on. */
export function newCustomSchemeId(): string {
  return `custom-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 8)}`
}
