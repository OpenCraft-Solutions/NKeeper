import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { DesktopView, desktopStyles } from './DesktopView'

const plugin = definePlugin({
  id: 'desktop',
  activate() {
    adoptStyles(desktopStyles)
  },
  sessionViews: { rdp: DesktopView },
})

export default plugin
