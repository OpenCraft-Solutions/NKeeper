import { useRef, useState, type CSSProperties, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { Alert } from '../ui/Alert'
import { Button } from '../ui/Button'
import { DeleteButton } from '../ui/DeleteButton'
import { Dialog } from '../ui/Dialog'
import { TextField } from '../ui/TextField'
import field from '../ui/TextField.module.css'
import {
  activeColors,
  COLOR_SCHEME_PRESETS,
  contrast,
  DEFAULT_COLOR_SCHEME,
  deriveTokens,
  GOOD_TEXT_CONTRAST,
  MAX_CUSTOM_COLOR_SCHEMES,
  MIN_TEXT_CONTRAST,
  newCustomSchemeId,
  previewStyle,
  type ColorSchemeColors,
  type ColorSchemePreferences,
  type CustomColorScheme,
} from './colorSchemes'
import styles from './ColorSchemeDialog.module.css'

/**
 * Choose a color scheme from the user's menu, or build your own (docs/architecture.md §7.14), ported from 1.x.
 * A choice applies at once (`onChange`) and is saved on the account.
 */
export function ColorSchemeDialog({
  preferences,
  onChange,
  onClose,
}: {
  preferences: ColorSchemePreferences
  onChange: (preferences: ColorSchemePreferences) => void
  onClose: () => void
}) {
  const [editing, setEditing] = useState<CustomColorScheme | null>(null)
  const [error, setError] = useState<string | null>(null)
  // Saves run in order, so quick changes (arrow keys over the list) end on the last one.
  const saving = useRef<Promise<unknown>>(Promise.resolve())

  function save(next: ColorSchemePreferences) {
    onChange(next)
    setError(null)
    saving.current = saving.current
      .catch(() => undefined)
      .then(() => api.put('/api/account/color-schemes', next))
      .catch((failure: unknown) => setError(failure instanceof ApiError ? failure.message : 'Your color scheme could not be saved.'))
  }

  function saveCustomScheme(scheme: CustomColorScheme) {
    const exists = preferences.custom.some((other) => other.id === scheme.id)
    save({
      active: scheme.id,
      custom: exists ? preferences.custom.map((other) => (other.id === scheme.id ? scheme : other)) : [...preferences.custom, scheme],
    })
    setEditing(null)
  }

  function deleteCustomScheme(id: string) {
    save({
      active: preferences.active === id ? DEFAULT_COLOR_SCHEME : preferences.active,
      custom: preferences.custom.filter((scheme) => scheme.id !== id),
    })
  }

  if (editing) {
    return <SchemeBuilder scheme={editing} isNew={!preferences.custom.some((scheme) => scheme.id === editing.id)} onCancel={() => setEditing(null)} onSave={saveCustomScheme} />
  }

  const canAdd = preferences.custom.length < MAX_CUSTOM_COLOR_SCHEMES

  return (
    <Dialog
      title="Color scheme"
      description="Follows your account to any browser you sign in on."
      onClose={onClose}
      footer={<Button onClick={onClose}>Done</Button>}
    >
      <div className={styles.content}>
        {error ? <Alert>{error}</Alert> : null}
        <fieldset className={styles.group}>
          <legend className={styles.legend}>Presets</legend>
          <div className={styles.options}>
            {COLOR_SCHEME_PRESETS.map((preset) => (
              <SchemeOption key={preset.id} id={preset.id} name={preset.name} colors={preset} active={activeColors(preferences) === preset} onChoose={() => save({ ...preferences, active: preset.id })} />
            ))}
          </div>
        </fieldset>
        <fieldset className={styles.group}>
          <legend className={styles.legend}>Your color schemes</legend>
          {preferences.custom.length === 0 ? <p className={styles.hint}>Build your own from four colors.</p> : null}
          {preferences.custom.map((scheme) => (
            <div key={scheme.id} className={styles.customRow}>
              <SchemeOption id={scheme.id} name={scheme.name} colors={scheme} active={activeColors(preferences) === scheme} onChoose={() => save({ ...preferences, active: scheme.id })} />
              <Button variant="secondary" size="sm" aria-label={`Edit ${scheme.name}`} onClick={() => setEditing(scheme)}>
                Edit
              </Button>
              <DeleteButton question={`Delete ${scheme.name}?`} loading={false} onConfirm={() => deleteCustomScheme(scheme.id)} />
            </div>
          ))}
          <div>
            <Button
              variant="secondary"
              disabled={!canAdd}
              // Starts from the current colors, so a small change is a small effort.
              onClick={() => setEditing({ ...pickColors(activeColors(preferences)), id: newCustomSchemeId(), name: '' })}
            >
              New color scheme
            </Button>
          </div>
          {canAdd ? null : <p className={styles.hint}>You can keep up to {MAX_CUSTOM_COLOR_SCHEMES} color schemes. Delete one to add another.</p>}
        </fieldset>
      </div>
    </Dialog>
  )
}

function pickColors({ background, surface, text, accent }: ColorSchemeColors): ColorSchemeColors {
  return { background, surface, text, accent }
}

interface SchemeOptionProps {
  id: string
  name: string
  colors: ColorSchemeColors
  active: boolean
  onChoose: () => void
}

function SchemeOption({ id, name, colors, active, onChoose }: SchemeOptionProps) {
  return (
    <label className={`${styles.option} ${active ? styles.optionActive : ''}`}>
      <input type="radio" className="visually-hidden" name="color-scheme" value={id} checked={active} onChange={onChoose} />
      <span className={styles.swatch} style={{ background: colors.background, borderColor: colors.surface }} aria-hidden="true">
        <span className={styles.swatchPanel} style={{ background: colors.surface, color: colors.text }}>
          Aa
        </span>
        <span className={styles.swatchAccent} style={{ background: colors.accent }} />
      </span>
      <span className={styles.optionName}>{name}</span>
    </label>
  )
}

const COLOR_FIELDS: { key: keyof ColorSchemeColors; label: string; hint: string }[] = [
  { key: 'background', label: 'Background', hint: 'Behind everything' },
  { key: 'surface', label: 'Panels', hint: 'Cards, menus and dialogs' },
  { key: 'text', label: 'Text', hint: 'Most of what you read' },
  { key: 'accent', label: 'Accent', hint: 'Buttons, links and highlights' },
]

function SchemeBuilder({ scheme, isNew, onCancel, onSave }: { scheme: CustomColorScheme; isNew: boolean; onCancel: () => void; onSave: (scheme: CustomColorScheme) => void }) {
  const [name, setName] = useState(scheme.name)
  const [colors, setColors] = useState<ColorSchemeColors>(() => pickColors(scheme))
  const [nameError, setNameError] = useState<string>()

  const worstContrast = Math.min(contrast(colors.text, colors.background), contrast(colors.text, colors.surface))
  const unreadable = worstContrast < MIN_TEXT_CONTRAST

  function handleSubmit(event: FormEvent) {
    event.preventDefault()
    const trimmed = name.trim()
    if (!trimmed) {
      setNameError('Give the color scheme a name.')
      return
    }
    if (!unreadable) {
      onSave({ ...colors, id: scheme.id, name: trimmed })
    }
  }

  const tokens = deriveTokens(colors)

  return (
    <Dialog
      title={isNew ? 'New color scheme' : `Edit ${scheme.name}`}
      description="Pick four colors. The rest of the palette is worked out from them and kept readable."
      onClose={onCancel}
      footer={
        <>
          <Button variant="secondary" onClick={onCancel}>
            Cancel
          </Button>
          <Button type="submit" form="color-scheme-builder" disabled={unreadable}>
            Save and use
          </Button>
        </>
      }
    >
      <form id="color-scheme-builder" className={styles.content} onSubmit={handleSubmit} noValidate>
        <TextField
          label="Name"
          value={name}
          maxLength={40}
          error={nameError}
          data-autofocus
          onChange={(event) => {
            setName(event.target.value)
            setNameError(undefined)
          }}
        />
        <div className={styles.colorFields}>
          {COLOR_FIELDS.map(({ key, label, hint }) => (
            <ColorField key={key} label={label} hint={hint} value={colors[key]} onChange={(value) => setColors((current) => ({ ...current, [key]: value }))} />
          ))}
        </div>
        {unreadable ? (
          <Alert>The text is too close in color to the background or panels to read. Change one of them to save this scheme.</Alert>
        ) : worstContrast < GOOD_TEXT_CONTRAST ? (
          <Alert variant="info">The text may be hard to read against the background or panels. More contrast would help.</Alert>
        ) : null}
        <SchemePreview style={previewStyle(tokens) as CSSProperties} />
      </form>
    </Dialog>
  )
}

function ColorField({ label, hint, value, onChange }: { label: string; hint: string; value: string; onChange: (value: string) => void }) {
  // What's typed may be half a color; only a complete one reaches the scheme.
  const [typed, setTyped] = useState<string | null>(null)
  return (
    <div className={field.field}>
      <span className={field.label}>{label}</span>
      <div className={styles.colorInputs}>
        <input
          type="color"
          className={styles.colorPicker}
          aria-label={`${label} color picker`}
          value={value}
          onChange={(event) => {
            setTyped(null)
            onChange(event.target.value)
          }}
        />
        <input
          className={`${field.input} ${styles.colorHex}`}
          aria-label={`${label} hex color`}
          spellCheck={false}
          maxLength={7}
          value={typed ?? value}
          onChange={(event) => {
            const next = event.target.value.trim()
            setTyped(next)
            if (/^#[0-9a-fA-F]{6}$/.test(next)) {
              onChange(next.toLowerCase())
            }
          }}
          onBlur={() => setTyped(null)}
        />
      </div>
      <span className={styles.hint}>{hint}</span>
    </div>
  )
}

/** A small sample of the app in the scheme being built; the rest of the app keeps its colors until it's saved. */
function SchemePreview({ style }: { style: CSSProperties }) {
  return (
    <div className={styles.preview} style={style} role="group" aria-label="Preview">
      <div className={styles.previewPanel}>
        <strong>Preview</strong>
        <span className={styles.previewMuted}>Secondary text and a <span className={styles.previewLink}>link</span></span>
        <span className={styles.previewStates}>
          <span className={styles.previewSuccess}>Online</span>
          <span className={styles.previewWarning}>Warning</span>
          <span className={styles.previewDanger}>Failed</span>
        </span>
        <span className={styles.previewActions}>
          <span className={styles.previewPrimary}>Primary</span>
          <span className={styles.previewSecondary}>Secondary</span>
        </span>
      </div>
    </div>
  )
}
