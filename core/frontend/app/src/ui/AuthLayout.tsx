import type { ReactNode } from 'react'
import { PRODUCT_TAGLINE } from './brand'
import { Logo } from './Logo'
import styles from './AuthLayout.module.css'

interface AuthLayoutProps {
  title: string
  children: ReactNode
  footer?: ReactNode
}

/** Centered single-card layout for signed-out pages. */
export function AuthLayout({ title, children, footer }: AuthLayoutProps) {
  return (
    <div className={styles.page}>
      <main className={styles.main}>
        <header className={styles.brand}>
          <Logo size={40} />
          <p className={styles.tagline}>{PRODUCT_TAGLINE}</p>
        </header>

        <section className={styles.card} aria-labelledby="auth-title">
          <h1 id="auth-title" className={styles.title}>
            {title}
          </h1>
          {children}
          {footer ? <div className={styles.footer}>{footer}</div> : null}
        </section>
      </main>
    </div>
  )
}
