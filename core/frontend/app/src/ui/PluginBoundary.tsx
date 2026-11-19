import { Component, type ReactNode } from 'react'
import { Alert } from './Alert'

interface Props {
  pluginId: string
  /** A change clears the failure, e.g. another host. */
  resetKey?: string
  children: ReactNode
}

/** A plugin's component that throws while drawing says so in its place, and the rest of the page goes on. */
export class PluginBoundary extends Component<Props, { failed: string | null; key: string | undefined }> {
  state = { failed: null as string | null, key: this.props.resetKey }

  static getDerivedStateFromError(error: unknown) {
    return { failed: error instanceof Error ? error.message : 'It failed.' }
  }

  static getDerivedStateFromProps(props: Props, state: { failed: string | null; key: string | undefined }) {
    return props.resetKey === state.key ? null : { failed: null, key: props.resetKey }
  }

  render() {
    return this.state.failed !== null ? <Alert>{`The ${this.props.pluginId} plugin couldn’t draw this: ${this.state.failed}`}</Alert> : this.props.children
  }
}
