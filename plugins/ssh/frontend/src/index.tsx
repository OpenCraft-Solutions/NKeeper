import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import xtermStyles from '@xterm/xterm/css/xterm.css?inline'
import { TerminalView, terminalStyles } from './TerminalView'

const plugin = definePlugin({
  id: 'ssh',
  activate() {
    // Constructable stylesheets: the shell's policy blocks <style> elements (docs/architecture.md §7.14).
    adoptStyles(xtermStyles)
    adoptStyles(terminalStyles)
  },
  sessionViews: { ssh: TerminalView },
})

export default plugin
