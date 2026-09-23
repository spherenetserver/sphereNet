import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { en } from './en'
import { tr } from './tr'
import {
  STORAGE_KEY, detectLocale, fmtNumber, initLocale, interpolate, locale, readSavedLocale, setLocale, t,
} from './index'

const placeholders = (s: string) => [...s.matchAll(/\{(\w+)\}/g)].map(m => m[1]).sort()

describe('dictionaries', () => {
  it('have identical key sets', () => {
    expect(Object.keys(tr).sort()).toEqual(Object.keys(en).sort())
  })

  it('use the same placeholders for every key', () => {
    for (const key of Object.keys(en) as (keyof typeof en)[]) {
      expect(placeholders(tr[key]), key).toEqual(placeholders(en[key]))
    }
  })

  it('have no empty messages', () => {
    for (const [key, value] of [...Object.entries(en), ...Object.entries(tr)]) {
      expect(value.trim(), key).not.toBe('')
    }
  })
})

describe('interpolation', () => {
  beforeEach(() => setLocale('en'))

  it('fills named placeholders, repeated ones too', () => {
    expect(interpolate('{a} and {b}, {a} again', { a: 1, b: 'two' })).toBe('1 and two, 1 again')
  })

  it('leaves unknown placeholders and templates without params alone', () => {
    expect(interpolate('Hello {name}', { other: 'x' })).toBe('Hello {name}')
    expect(interpolate('Hello {name}')).toBe('Hello {name}')
  })

  it('t() interpolates in the active language', () => {
    expect(t('players.online', { n: 3 })).toBe('3 online')
    setLocale('tr')
    expect(t('players.online', { n: 3 })).toBe('3 çevrimiçi')
  })

  it('formats numbers for the active locale', () => {
    setLocale('en')
    expect(fmtNumber(1234.5)).toBe('1,234.5')
    setLocale('tr')
    expect(fmtNumber(1234.5)).toBe('1.234,5')
    expect(fmtNumber(undefined)).toBe('—')
  })
})

describe('locale detection and persistence', () => {
  beforeEach(() => localStorage.clear())
  afterEach(() => vi.restoreAllMocks())

  it('prefers the saved choice, then the browser language, then English', () => {
    expect(detectLocale('en', 'tr-TR')).toBe('en')
    expect(detectLocale('tr', 'en-US')).toBe('tr')
    expect(detectLocale(null, 'tr-TR')).toBe('tr')
    expect(detectLocale(null, 'TR')).toBe('tr')
    expect(detectLocale(null, 'de-DE')).toBe('en')
    expect(detectLocale(null, undefined)).toBe('en')
    expect(detectLocale('fr', 'tr')).toBe('tr') // invalid saved value is ignored
  })

  it('persists the choice and restores it on the next start', () => {
    setLocale('tr')
    expect(localStorage.getItem(STORAGE_KEY)).toBe('tr')
    expect(document.documentElement.lang).toBe('tr')

    locale.value = 'en' // simulate a fresh page whose default differs
    expect(initLocale()).toBe('tr')
    expect(locale.value).toBe('tr')
  })

  it('falls back to the browser language when nothing is saved', () => {
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('tr-TR')
    expect(initLocale()).toBe('tr')
    vi.spyOn(navigator, 'language', 'get').mockReturnValue('en-GB')
    expect(initLocale()).toBe('en')
    expect(document.documentElement.lang).toBe('en')
  })

  it('survives storage that throws', () => {
    vi.spyOn(Storage.prototype, 'getItem').mockImplementation(() => { throw new Error('blocked') })
    vi.spyOn(Storage.prototype, 'setItem').mockImplementation(() => { throw new Error('blocked') })
    expect(readSavedLocale()).toBeNull()
    expect(() => setLocale('tr')).not.toThrow()
    expect(locale.value).toBe('tr')
  })
})
