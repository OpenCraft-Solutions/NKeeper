import type { ReactNode } from 'react'
import styles from './Badge.module.css'

interface BadgeProps {
  variant?: 'neutral' | 'accent' | 'tag'
  title?: string
  children: ReactNode
}

/** Small inline label for services, tags and statuses. */
export function Badge({ variant = 'neutral', title, children }: BadgeProps) {
  return (
    <span className={`${styles.badge} ${styles[variant]}`} title={title}>
      {children}
    </span>
  )
}
