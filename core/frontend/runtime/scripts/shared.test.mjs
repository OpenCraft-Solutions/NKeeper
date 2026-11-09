import assert from 'node:assert/strict'
import { test } from 'node:test'
import { importMap, reexport } from './shared.mjs'

test('re-exports a CommonJS module with its valid named exports, sorted', () => {
  const source = reexport('react', ['useState', 'default', '__esModule', 'Children', 'not-an-identifier', 'useState'])

  assert.match(source, /import shared from 'react'/)
  assert.match(source, /export default shared/)
  assert.match(source, /export const \{ Children, useState \} = shared/)
})

test('builds an import map with absolute URLs and sorted keys', () => {
  const map = importMap({ react: 'runtime/react-1.js', '@nkeeper/sdk': 'runtime/sdk-2.js' })

  assert.deepEqual(Object.keys(map.imports), ['@nkeeper/sdk', 'react'])
  assert.equal(map.imports.react, '/runtime/react-1.js')
})
