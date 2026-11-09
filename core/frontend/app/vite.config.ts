import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv } from 'vite'

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')

  return {
    plugins: [react()],
    server: {
      port: 5173,
      strictPort: true,
      // In development the browser talks to Vite only, and /api is forwarded to the server, so
      // cookies stay same-origin as they are behind nginx in production.
      proxy: {
        '/api': {
          target: env.DEV_API_PROXY_TARGET || 'http://localhost:5080',
          changeOrigin: false,
          ws: true,
        },
      },
    },
    test: {
      environment: 'jsdom',
      setupFiles: ['./src/test/setup.ts'],
      restoreMocks: true,
      unstubGlobals: true,
    },
  }
})
