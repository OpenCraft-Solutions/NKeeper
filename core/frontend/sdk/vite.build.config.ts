import { defineConfig } from 'vite'

/**
 * Builds the publishable frontend SDK's one module, dist/index.js. The type declarations come from tsc
 * (tsconfig.build.json) and dist/package.json from scripts/package.mjs, which together make the package
 * @opencraft-solutions/nkeeper-sdk (docs/architecture.md §9). React stays external.
 */
export default defineConfig({
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2023',
    minify: false,
    sourcemap: false,
    lib: { entry: 'src/index.ts', formats: ['es'], fileName: () => 'index.js' },
    rollupOptions: { external: ['react', 'react/jsx-runtime', 'react-dom', 'react-dom/client'] },
  },
})
