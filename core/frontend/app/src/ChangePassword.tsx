import { useState, type FormEvent } from 'react'
import { api, ApiError } from '@nkeeper/sdk'
import { Alert } from './ui/Alert'
import { Button } from './ui/Button'
import form from './ui/Form.module.css'
import { PasswordField } from './ui/PasswordField'

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
    <form className={form.form} onSubmit={(event) => void submit(event)} noValidate aria-label="Change your password">
      <p className={form.lead}>An administrator asked you to choose a new password before you go on.</p>
      {error && <Alert>{error}</Alert>}
      <PasswordField label="Current password" autoComplete="current-password" required value={currentPassword} onChange={(e) => setCurrentPassword(e.target.value)} />
      <PasswordField label="New password" autoComplete="new-password" required value={newPassword} onChange={(e) => setNewPassword(e.target.value)} />
      <PasswordField label="Confirm the new password" autoComplete="new-password" required value={confirm} onChange={(e) => setConfirm(e.target.value)} />
      <Button type="submit" fullWidth loading={busy}>
        Change password
      </Button>
    </form>
  )
}
