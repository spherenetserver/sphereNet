import { beforeEach, describe, expect, it, vi } from 'vitest'

vi.mock('@/router', () => ({ default: { push: vi.fn() } }))

import { apiPath, errorMessage } from './api'
import { setLocale } from '@/i18n'

describe('apiPath', () => {
  it('encodes each segment so it stays one path segment', () => {
    expect(apiPath('/accounts', 'a/b c', 'ban')).toBe('/accounts/a%2Fb%20c/ban')
    expect(apiPath('/accounts', 'x?y#z%')).toBe('/accounts/x%3Fy%23z%25')
  })

  it('encodes IPv6 colons and passes numbers through', () => {
    expect(apiPath('/ipblocks', '2001:db8::1')).toBe('/ipblocks/2001%3Adb8%3A%3A1')
    expect(apiPath('/players', 1234, 'disconnect')).toBe('/players/1234/disconnect')
  })

  it('leaves plain names alone', () => {
    expect(apiPath('/accounts', 'admin', 'password')).toBe('/accounts/admin/password')
  })
})

describe('errorMessage', () => {
  beforeEach(() => setLocale('en'))

  it('prefers the backend error body, then the HTTP status', () => {
    expect(errorMessage({ response: { status: 409, data: { error: 'Account already exists' } } }))
      .toBe('Account already exists')
    expect(errorMessage({ response: { status: 404, data: '' } }, 'Ban failed')).toBe('Ban failed (HTTP 404)')
    expect(errorMessage({ message: 'Network Error' }, 'Ban failed')).toBe('Ban failed: Network Error')
  })

  it('uses the active language for the default fallback', () => {
    expect(errorMessage({})).toBe('Request failed')
    setLocale('tr')
    expect(errorMessage({})).toBe('İstek başarısız')
  })
})
