import { adoptStyles, definePlugin } from '@nkeeper/sdk'
import { WebView, webStyles } from './WebView'

const plugin = definePlugin({
  id: 'web',
  activate() {
    adoptStyles(webStyles)
  },
  sessionViews: { web: WebView },
})

export default plugin
