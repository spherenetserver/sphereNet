import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it, vi } from 'vitest'

const mocks = vi.hoisted(() => ({
  login: vi.fn(),
  logout: vi.fn(),
  push: vi.fn(),
}))

vi.mock('@/lib/api', () => ({
  authApi: {
    login: mocks.login,
    logout: mocks.logout,
  },
}))

vi.mock('@/router', () => ({
  default: {
    push: mocks.push,
  },
}))

import { useAuthStore } from './auth'

describe('auth store', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('sends the original bearer token while clearing the local session immediately', async () => {
    localStorage.setItem('sn_token', 'token-to-revoke')
    localStorage.setItem('sn_server', 'Test Shard')
    const auth = useAuthStore()
    mocks.logout.mockResolvedValueOnce({})

    await auth.logout()

    expect(mocks.logout).toHaveBeenCalledWith('token-to-revoke')
    expect(auth.token).toBeNull()
    expect(auth.loggedIn).toBe(false)
    expect(localStorage.getItem('sn_token')).toBeNull()
    expect(mocks.push).toHaveBeenCalledWith('/login')
  })

  it('persists the token returned by login', async () => {
    mocks.login.mockResolvedValueOnce({
      data: { token: 'new-token', serverName: 'Test Shard' },
    })
    const auth = useAuthStore()

    await auth.login('secret')

    expect(auth.loggedIn).toBe(true)
    expect(auth.token).toBe('new-token')
    expect(localStorage.getItem('sn_token')).toBe('new-token')
    expect(mocks.push).toHaveBeenCalledWith('/dashboard')
  })
})
