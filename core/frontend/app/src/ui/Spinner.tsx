import styles from './Spinner.module.css'

interface SpinnerProps {
  size?: 'sm' | 'md'
  /** Accessible label. Omit when the spinner sits next to visible status text. */
  label?: string
}

export function Spinner({ size = 'md', label }: SpinnerProps) {
  return (
    <span
      className={`${styles.spinner} ${size === 'sm' ? styles.small : ''}`}
      role={label ? 'status' : undefined}
      aria-label={label}
      aria-hidden={label ? undefined : true}
    />
  )
}
