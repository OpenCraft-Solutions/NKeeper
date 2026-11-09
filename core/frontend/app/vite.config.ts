import { readFileSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import react from '@vitejs/plugin-react'
import { defineConfig, loadEnv, type Plugin } from 'vite'

/** The built @nkeeper/runtime: hashed shared modules under runtime/, and importmap.json. */
const runtimeDist = fileURLToPath(new URL('../runtime/dist', import.meta.url))

/**
 * Production builds share React, ReactDOM, the JSX runtime and the SDK with plugins through the import
 * map (docs/plugin-security.md §7.2): the shell imports them by name like a plugin does, the runtime's
 * files are copied into the output, and the map is inlined first in <head>. Tests and the dev server
 * resolve them normally instead.
 */
function sharedRuntime(): Plugin {
  let importMap: { imports: Record<string, string> } = { imports: {} }
  return {
    name: 'nkeeper-shared-runtime',
    apply: 'build',
    config() {
      try {
        importMap = JSON.parse(readFileSync(`${runtimeDist}/importmap.json`, 'utf8')) as typeof importMap
      } catch {
        throw new Error('Build @nkeeper/runtime first (npm run build at the repository root builds both).')
      }
      return {
        publicDir: runtimeDist,
        build: { rollupOptions: { external: Object.keys(importMap.imports) } },
      }
    },
    transformIndexHtml: {
      order: 'pre',
      handler: () => [{ tag: 'script', attrs: { type: 'importmap' }, children: JSON.stringify(importMap), injectTo: 'head-prepend' }],
    },
  }
}

// https://vite.dev/config/
export default defineConfig(({ mode }) => {
  const env = loadEnv(mode, process.cwd(), '')

  return {
    plugins: [react(), sharedRuntime()],
    build: {
      target: 'es2023',
      // Vite's modulepreload polyfill isn't needed by the browsers NKeeper supports.
      modulePreload: { polyfill: false },
    },
    server: {
      port: 5173,
      strictPort: true,
      // The dev server is for work on the shell alone; plugins load only through the NKeeper server
      // (docs/plugin-security.md §7.3). /api is forwarded so the shell can talk to it.
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
