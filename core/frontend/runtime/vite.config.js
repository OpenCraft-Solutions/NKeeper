// Builds the shared modules as hashed ES modules under dist/runtime/, and dist/importmap.json naming
// them. The app build copies dist/ into its output and inlines the import map (docs/plugin-security.md §7.2).
import { defineConfig } from 'vite'
import { SHARED, importMap } from './scripts/shared.mjs'

const entries = Object.fromEntries(Object.values(SHARED).map((entry) => [entry, `src/generated/${entry}.js`]))

/** Emits importmap.json from the entry chunks' final, hashed file names. */
function emitImportMap() {
  return {
    name: 'nkeeper-import-map',
    generateBundle(_options, bundle) {
      const fileByEntry = {}
      for (const chunk of Object.values(bundle)) {
        if (chunk.type === 'chunk' && chunk.isEntry) {
          fileByEntry[chunk.name] = chunk.fileName
        }
      }
      const fileBySpecifier = Object.fromEntries(Object.entries(SHARED).map(([specifier, entry]) => [specifier, fileByEntry[entry]]))
      const missing = Object.entries(fileBySpecifier).filter(([, file]) => !file)
      if (missing.length) {
        this.error(`No entry chunk for ${missing.map(([specifier]) => specifier).join(', ')}`)
      }
      this.emitFile({ type: 'asset', fileName: 'importmap.json', source: JSON.stringify(importMap(fileBySpecifier), null, 2) + '\n' })
    },
  }
}

export default defineConfig({
  plugins: [emitImportMap()],
  build: {
    outDir: 'dist',
    emptyOutDir: true,
    target: 'es2023',
    rollupOptions: {
      input: entries,
      // Keep every export of every entry: plugins import names the shell itself may never use.
      preserveEntrySignatures: 'strict',
      output: {
        format: 'es',
        entryFileNames: 'runtime/[name]-[hash].js',
        chunkFileNames: 'runtime/shared-[hash].js',
      },
    },
  },
})
