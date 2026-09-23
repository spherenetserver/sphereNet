import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { HISTORY_KEY, HISTORY_LIMIT, pushHistory, readHistory, saveHistory } from './commandHistory'

describe('command history', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => vi.restoreAllMocks())

  it('moves a repeated command to the front instead of duplicating it', () => {
    expect(pushHistory(['b', 'a'], 'a')).toEqual(['a', 'b'])
    expect(pushHistory(['a'], '  ')).toEqual(['a'])
  })

  it('keeps at most the limit', () => {
    let h: string[] = []
    for (let i = 0; i < HISTORY_LIMIT + 5; i++) h = pushHistory(h, `cmd${i}`)
    expect(h).toHaveLength(HISTORY_LIMIT)
    expect(h[0]).toBe(`cmd${HISTORY_LIMIT + 4}`)
  })

  it('round-trips through storage and ignores junk', () => {
    saveHistory(['save', 'status'])
    expect(readHistory()).toEqual(['save', 'status'])
    localStorage.setItem(HISTORY_KEY, '{not json')
    expect(readHistory()).toEqual([])
    localStorage.setItem(HISTORY_KEY, JSON.stringify([1, 'ok', null]))
    expect(readHistory()).toEqual(['ok'])
  })

  it('survives storage that throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    expect(readHistory()).toEqual([])
    expect(() => saveHistory(['x'])).not.toThrow()
  })
})
