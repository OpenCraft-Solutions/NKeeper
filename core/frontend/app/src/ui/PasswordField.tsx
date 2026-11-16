import { useState } from 'react'
import { TextField, type TextFieldProps } from './TextField'
import styles from './PasswordField.module.css'

type PasswordFieldProps = Omit<TextFieldProps, 'type' | 'trailing'>

export function PasswordField(props: PasswordFieldProps) {
  const [visible, setVisible] = useState(false)

  return (
    <TextField
      {...props}
      type={visible ? 'text' : 'password'}
      trailing={
        <button
          type="button"
          className={styles.toggle}
          onClick={() => setVisible((v) => !v)}
          aria-pressed={visible}
          aria-label={visible ? 'Hide password' : 'Show password'}
        >
          {visible ? 'Hide' : 'Show'}
        </button>
      }
    />
  )
}
