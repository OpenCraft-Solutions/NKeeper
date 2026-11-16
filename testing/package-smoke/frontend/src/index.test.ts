import { isPluginDefinition } from '@nkeeper/sdk'
import { expect, it } from 'vitest'
import plugin from './index'

it('is a plugin definition the published SDK accepts', () => {
  expect(isPluginDefinition(plugin)).toBe(true)
  expect(plugin.id).toBe('com.example.smoke')
})
