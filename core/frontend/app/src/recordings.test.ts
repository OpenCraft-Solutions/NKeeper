import { describe, expect, it } from 'vitest'
import { parseCast } from './recordings'

describe('terminal recordings (1.x)', () => {
  it('reads an asciicast’s size, output and resizes, skipping what it doesn’t know', () => {
    const cast = parseCast(
      [
        JSON.stringify({ version: 2, width: 80, height: 24, timestamp: 1, title: 'SSH web01' }),
        JSON.stringify([0.5, 'r', '120x40']),
        JSON.stringify([1.25, 'o', 'héllo\r\n']),
        JSON.stringify([2, 'i', 'typed']),
        'not json',
        '',
      ].join('\n'),
    )
    expect(cast).toEqual({
      width: 80,
      height: 24,
      events: [
        { seconds: 0.5, kind: 'r', text: '120x40', columns: 120, rows: 40 },
        { seconds: 1.25, kind: 'o', text: 'héllo\r\n' },
      ],
    })
    expect(parseCast('{"version":1}')).toBeNull()
    expect(parseCast('')).toBeNull()
  })
})
