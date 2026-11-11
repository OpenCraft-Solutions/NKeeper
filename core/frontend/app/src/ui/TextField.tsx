import { useId, type InputHTMLAttributes, type ReactNode, type Ref } from 'react'
import styles from './TextField.module.css'

export interface TextFieldProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'id'> {
  label: string
  /** Render the label for screen readers only (e.g. in compact toolbars). */
  hideLabel?: boolean
  error?: string
  hint?: string
  /** Optional element rendered inside the input on the right (e.g. a show/hide toggle). */
  trailing?: ReactNode
  ref?: Ref<HTMLInputElement>
}

/** Labelled input whose validation error is announced and linked via aria-describedby. */
export function TextField({ label, hideLabel = false, error, hint, trailing, className, ref, ...inputProps }: TextFieldProps) {
  const id = useId()
  const errorId = `${id}-error`
  const hintId = `${id}-hint`
  const describedBy = [error ? errorId : null, hint ? hintId : null].filter(Boolean).join(' ') || undefined

  return (
    <div className={`${styles.field} ${className ?? ''}`}>
      <label htmlFor={id} className={hideLabel ? 'visually-hidden' : styles.label}>
        {label}
      </label>
      <div className={styles.control}>
        <input
          ref={ref}
          id={id}
          className={`${styles.input} ${trailing ? styles.withTrailing : ''}`}
          aria-invalid={error ? true : undefined}
          aria-describedby={describedBy}
          {...inputProps}
        />
        {trailing ? <div className={styles.trailing}>{trailing}</div> : null}
      </div>
      {hint ? (
        <p id={hintId} className={styles.hint}>
          {hint}
        </p>
      ) : null}
      {error ? (
        <p id={errorId} className={styles.error}>
          {error}
        </p>
      ) : null}
    </div>
  )
}
