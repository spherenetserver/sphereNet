import { beforeEach, describe, expect, it, vi } from 'vitest'
import { hexId, hexSerial, layerName, parseSerial, readFramePreference, saveFramePreference } from './paperdoll'

describe('parseSerial', () => {
  it.each([
    ['123', 123],
    [' 0x1A2B ', 0x1a2b],
    ['0X3fffffff', 0x3fffffff],
    ['01a2b', 0x1a2b],
    ['010', 0x10],
  ])('parses %s', (input, expected) => expect(parseSerial(input)).toBe(expected))

  it.each(['', '0', '0x0', '0x40000000', '-5', '+5', 'abc', '1.5', '12a', '0x'])(
    'rejects %s', input => expect(parseSerial(input)).toBeNull())
})

describe('formatting', () => {
  it('pads serials and ids', () => {
    expect(hexSerial(0x1a2b)).toBe('0x00001A2B')
    expect(hexId(0x1f03)).toBe('0x1F03')
  })

  it('names layers and falls back to the number', () => {
    expect(layerName(22)).toBe('Robe')
    expect(layerName(99)).toBe('Layer 99')
  })
})

describe('frame preference', () => {
  beforeEach(() => localStorage.clear())

  it('defaults to on and remembers the choice', () => {
    expect(readFramePreference()).toBe(true)
    saveFramePreference(false)
    expect(readFramePreference()).toBe(false)
    saveFramePreference(true)
    expect(readFramePreference()).toBe(true)
  })

  it('falls back to on when storage throws', () => {
    const spy = vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => {
      throw new Error('blocked')
    })
    try {
      expect(readFramePreference()).toBe(true)
    } finally {
      spy.mockRestore()
    }
  })
})
