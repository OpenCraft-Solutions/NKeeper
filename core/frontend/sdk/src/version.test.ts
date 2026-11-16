/// <reference types="node" />
import { readFileSync } from 'node:fs'
import { describe, expect, it } from 'vitest'
import { SDK_VERSION } from './plugin'

const read = (path: string) => readFileSync(new URL(path, import.meta.url), 'utf8')

describe('the SDK version', () => {
  it('is the workspace package version and the backend SDK version (docs/architecture.md §4)', () => {
    const workspace = JSON.parse(read('../package.json')) as { version: string }
    const backend = /Current\s*=\s*"([^"]+)"/.exec(read('../../../backend/NKeeper.Abstractions/SdkVersion.cs'))?.[1]

    expect(workspace.version).toBe(SDK_VERSION)
    expect(backend).toBe(SDK_VERSION)
  })
})
