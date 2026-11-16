import { render, screen, waitFor, within } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import { useState } from 'react'
import { beforeEach, describe, expect, it } from 'vitest'
import { fakeApi, problem } from '../test/fixtures'
import type { ColorSchemePreferences } from './colorSchemes'
import { ColorSchemeDialog } from './ColorSchemeDialog'

const OCEAN = { id: 'custom-ocean', name: 'Ocean', background: '#001122', surface: '#112233', text: '#eeffff', accent: '#0088cc' }

/** Holds the choices the way the shell does, without a session behind it. */
function Harness({ initial }: { initial: ColorSchemePreferences }) {
  const [preferences, setPreferences] = useState(initial)
  return (
    <>
      <ColorSchemeDialog preferences={preferences} onChange={setPreferences} onClose={() => undefined} />
      <output data-testid="active">{preferences.active}</output>
    </>
  )
}

let fetch: ReturnType<typeof fakeApi>
let fail = false

/** What was saved, in order. */
const saved = () =>
  fetch.mock.calls.filter(([url, init]) => url === '/api/account/color-schemes' && init?.method === 'PUT').map(([, init]) => JSON.parse(init!.body as string) as ColorSchemePreferences)

describe('ColorSchemeDialog', () => {
  beforeEach(() => {
    fail = false
    fetch = fakeApi({ 'PUT /api/account/color-schemes': (init) => (fail ? problem(500, { detail: 'offline' }) : new Response(init?.body as string, { headers: { 'Content-Type': 'application/json' } })) })
  })

  it('applies and saves a preset as soon as it is chosen', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)
    expect(screen.getByRole('radio', { name: 'Navy' })).toBeChecked()

    await user.click(screen.getByRole('radio', { name: 'Light' }))

    expect(screen.getByRole('radio', { name: 'Light' })).toBeChecked()
    expect(screen.getByTestId('active')).toHaveTextContent('light')
    await waitFor(() => expect(saved()).toEqual([{ active: 'light', custom: [] }]))
  })

  it('builds a new scheme from the current colors, then saves and uses it', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'light', custom: [] }} />)

    await user.click(screen.getByRole('button', { name: 'New color scheme' }))
    expect(screen.getByLabelText('Background hex color')).toHaveValue('#f3f5f9')

    await user.type(screen.getByLabelText('Name'), 'Mine')
    const accent = screen.getByLabelText('Accent hex color')
    await user.clear(accent)
    await user.type(accent, '#AA0055')
    await user.click(screen.getByRole('button', { name: 'Save and use' }))

    await waitFor(() => expect(saved()).toHaveLength(1))
    const sent = saved()[0]!
    expect(sent.custom).toEqual([{ id: sent.active, name: 'Mine', background: '#f3f5f9', surface: '#ffffff', text: '#1a2233', accent: '#aa0055' }])
    expect(screen.getByRole('radio', { name: 'Mine' })).toBeChecked()
  })

  it('needs a name, and refuses colors the text cannot be read on', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)
    await user.click(screen.getByRole('button', { name: 'New color scheme' }))

    await user.click(screen.getByRole('button', { name: 'Save and use' }))
    expect(screen.getByText('Give the color scheme a name.')).toBeInTheDocument()

    const text = screen.getByLabelText('Text hex color')
    await user.clear(text)
    await user.type(text, '#111a2e')
    expect(screen.getByRole('alert')).toHaveTextContent(/too close/)
    expect(screen.getByRole('button', { name: 'Save and use' })).toBeDisabled()
    expect(saved()).toEqual([])
  })

  it('edits a scheme without touching the others', async () => {
    const user = userEvent.setup()
    const other = { ...OCEAN, id: 'custom-other', name: 'Other' }
    render(<Harness initial={{ active: 'navy', custom: [OCEAN, other] }} />)

    await user.click(screen.getByRole('button', { name: 'Edit Ocean' }))
    const name = screen.getByLabelText('Name')
    await user.clear(name)
    await user.type(name, 'Deep ocean')
    await user.click(screen.getByRole('button', { name: 'Save and use' }))

    await waitFor(() => expect(saved()).toEqual([{ active: OCEAN.id, custom: [{ ...OCEAN, name: 'Deep ocean' }, other] }]))
  })

  it('goes back to the default when the scheme in use is deleted', async () => {
    const user = userEvent.setup()
    render(<Harness initial={{ active: OCEAN.id, custom: [OCEAN] }} />)

    await user.click(screen.getByRole('button', { name: 'Delete' }))
    await user.click(within(screen.getByRole('group', { name: 'Confirm delete' })).getByRole('button', { name: 'Yes, delete' }))

    await waitFor(() => expect(saved()).toEqual([{ active: 'navy', custom: [] }]))
    expect(screen.getByRole('radio', { name: 'Navy' })).toBeChecked()
  })

  it('says so when the choice could not be saved', async () => {
    fail = true
    const user = userEvent.setup()
    render(<Harness initial={{ active: 'navy', custom: [] }} />)

    await user.click(screen.getByRole('radio', { name: 'Forest' }))

    expect(await screen.findByRole('alert')).toBeInTheDocument()
  })
})
