import { useEffect, useState } from 'react'
import { Button } from './Button'
import styles from './DeleteButton.module.css'

interface DeleteButtonProps {
  /** What the confirmation asks, e.g. "Delete this host?" */
  question: string
  onConfirm: () => void
  loading: boolean
  disabled?: boolean
  /** Ask now, as if clicked (the Delete key in the host tree); `onAsked` then clears it. */
  askNow?: boolean
  onAsked?: () => void
}

/** Two-step delete: the first click asks, the second deletes. */
export function DeleteButton({ question, onConfirm, loading, disabled, askNow, onAsked }: DeleteButtonProps) {
  const [confirming, setConfirming] = useState(false)
  if (askNow && !disabled && !confirming) {
    setConfirming(true)
  }
  useEffect(() => {
    if (askNow) {
      onAsked?.()
    }
  }, [askNow, onAsked])

  if (!confirming) {
    return (
      <Button variant="danger" onClick={() => setConfirming(true)} disabled={disabled}>
        Delete
      </Button>
    )
  }

  return (
    <div className={styles.confirm} role="group" aria-label="Confirm delete">
      <span>{question}</span>
      <Button variant="danger" size="sm" onClick={onConfirm} loading={loading} autoFocus>
        Yes, delete
      </Button>
      <Button variant="ghost" size="sm" onClick={() => setConfirming(false)} disabled={loading}>
        Keep
      </Button>
    </div>
  )
}
