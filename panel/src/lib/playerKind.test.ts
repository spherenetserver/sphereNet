import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { PLAYER_KIND_KEY, countKinds, ofKind, readPlayerKind, savePlayerKind } from './playerKind'

const list = [
  { name: 'a' },
  { name: 'b', isBot: true },
  { name: 'c', isBot: false },
  { name: 'd', isBot: true },
]

describe('player kind filter', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => vi.restoreAllMocks())

  it('starts on real players and remembers the choice', () => {
    expect(readPlayerKind()).toBe('players')
    savePlayerKind('bots')
    expect(readPlayerKind()).toBe('bots')
    localStorage.setItem(PLAYER_KIND_KEY, 'nonsense')
    expect(readPlayerKind()).toBe('players')
  })

  it('survives storage that throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    expect(readPlayerKind()).toBe('players')
    expect(() => savePlayerKind('all')).not.toThrow()
  })

  it('splits the list and counts each side', () => {
    expect(ofKind(list, 'players').map(p => p.name)).toEqual(['a', 'c'])
    expect(ofKind(list, 'bots').map(p => p.name)).toEqual(['b', 'd'])
    expect(ofKind(list, 'all')).toHaveLength(4)
    expect(countKinds(list)).toEqual({ players: 2, bots: 2 })
  })
})
