import type { ButtonHTMLAttributes } from 'react'
import { Spinner } from './Spinner'
import styles from './Button.module.css'

interface ButtonProps extends ButtonHTMLAttributes<HTMLButtonElement> {
  variant?: 'primary' | 'secondary' | 'danger' | 'ghost'
  size?: 'md' | 'sm'
  fullWidth?: boolean
  /** Shows a spinner, disables the button and marks it busy. */
  loading?: boolean
}

export function Button({
  variant = 'primary',
  size = 'md',
  fullWidth = false,
  loading = false,
  disabled,
  className,
  children,
  type = 'button',
  ...rest
}: ButtonProps) {
  const classes = [styles.button, styles[variant], size === 'sm' ? styles.small : '', fullWidth ? styles.fullWidth : '', className ?? '']
    .filter(Boolean)
    .join(' ')

  return (
    <button type={type} className={classes} disabled={disabled || loading} aria-busy={loading || undefined} {...rest}>
      {loading ? <Spinner size="sm" /> : null}
      {children}
    </button>
  )
}
