import { useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'

/** A required password change (docs/architecture.md §6.1, §7.14): nothing else works until it's done. */
export function ChangePassword({ onChanged }: { onChanged: () => void }) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    if (newPassword !== confirm) {
      setError('The new passwords don’t match.')
      return
    }
    setBusy(true)
    try {
      await api.post('/api/account/password', { currentPassword, newPassword })
      onChanged()
    } catch (caught) {
      const errors = caught instanceof ApiError ? Object.values(caught.problem?.errors ?? {}).flat() : []
      setError(errors.length > 0 ? errors.join(' ') : caught instanceof ApiError ? caught.message : 'The password could not be changed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <form className="nk-form nk-signin" onSubmit={(event) => void submit(event)} aria-label="Change your password">
      <h2>Change your password</h2>
      <p className="nk-muted">An administrator asked you to choose a new password before you go on.</p>
      <label>
        Current password
        <input type="password" autoComplete="current-password" value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} required />
      </label>
      <label>
        New password
        <input type="password" autoComplete="new-password" value={newPassword} onChange={(e) => setNewPassword(e.target.value)} required />
      </label>
      <label>
        Confirm the new password
        <input type="password" autoComplete="new-password" value={confirm} onChange={(e) => setConfirm(e.target.value)} required />
      </label>
      {error && <p role="alert">{error}</p>}
      <button type="submit" className="nk-primary" disabled={busy}>
        Change password
      </button>
    </form>
  )
}
