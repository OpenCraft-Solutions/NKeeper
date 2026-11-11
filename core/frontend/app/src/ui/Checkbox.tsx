import { useId, type InputHTMLAttributes } from 'react'
import styles from './Checkbox.module.css'

interface CheckboxProps extends Omit<InputHTMLAttributes<HTMLInputElement>, 'type' | 'id'> {
  label: string
}

export function Checkbox({ label, className, ...inputProps }: CheckboxProps) {
  const id = useId()
  return (
    <div className={`${styles.checkbox} ${className ?? ''}`}>
      <input id={id} type="checkbox" className={styles.input} {...inputProps} />
      <label htmlFor={id}>{label}</label>
    </div>
  )
}
