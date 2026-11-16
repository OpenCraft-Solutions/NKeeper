// Writes dist/package.json: the published package, @opencraft-solutions/nkeeper-sdk, which this repository's
// workspaces import as @nkeeper/sdk (docs/architecture.md §9). Its version is the workspace's, which the release
// workflow checks against SdkVersion.Current.
import { readFileSync, writeFileSync } from 'node:fs'

const workspace = JSON.parse(readFileSync(new URL('../package.json', import.meta.url), 'utf8'))

const published = {
  name: '@opencraft-solutions/nkeeper-sdk',
  version: workspace.version,
  description: workspace.description,
  license: workspace.license,
  type: 'module',
  sideEffects: false,
  repository: {
    type: 'git',
    url: 'git+https://github.com/OpenCraft-Solutions/NKeeper.git',
    directory: 'core/frontend/sdk',
  },
  publishConfig: { registry: 'https://npm.pkg.github.com' },
  main: './index.js',
  types: './index.d.ts',
  exports: { '.': { types: './index.d.ts', import: './index.js' } },
  peerDependencies: { react: '^19.0.0' },
}

writeFileSync(new URL('../dist/package.json', import.meta.url), `${JSON.stringify(published, null, 2)}\n`)
