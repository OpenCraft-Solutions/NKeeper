import { defineConfig } from 'vite'

// Builds dist/index.js, the module the package carries as frontend/index.js. The modules the shell shares stay
// external: the browser takes them from the shell's import map.
export default defineConfig({
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2023',
    lib: { entry: 'src/index.ts', formats: ['es'], fileName: () => 'index.js' },
    rollupOptions: { external: ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/client', '@nkeeper/sdk'] },
  },
})
