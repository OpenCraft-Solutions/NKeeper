import type { ReactNode } from 'react'
import styles from './Alert.module.css'

interface AlertProps {
  variant?: 'error' | 'warning' | 'info'
  children: ReactNode
}

/** Errors and warnings use role="alert" so screen readers announce them immediately. */
export function Alert({ variant = 'error', children }: AlertProps) {
  return (
    <div className={`${styles.alert} ${styles[variant]}`} role={variant === 'info' ? 'status' : 'alert'}>
      {children}
    </div>
  )
}
