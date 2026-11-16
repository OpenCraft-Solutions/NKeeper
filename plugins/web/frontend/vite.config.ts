import react from '@vitejs/plugin-react'
import { defineConfig } from 'vite'

/**
 * Builds the plugin's entry module, dist/index.js, which the package carries as frontend/index.js. The
 * modules the shell shares (docs/architecture.md §5) stay external: the browser takes them from the
 * shell's import map, so the plugin and the shell use one copy of each.
 */
const SHARED = ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/client', '@nkeeper/sdk']

export default defineConfig({
  plugins: [react()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2023',
    minify: true,
    lib: { entry: 'src/index.tsx', formats: ['es'], fileName: () => 'index.js' },
    rollupOptions: { external: SHARED },
  },
})
