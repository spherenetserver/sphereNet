import { createPinia, setActivePinia } from 'pinia'
import { beforeEach, describe, expect, it } from 'vitest'
import type { ServerStats } from '@/lib/api'
import { STATS_HISTORY_CAP, pushSample, useServerStore } from './server'

const stats = (n: number): ServerStats => ({
  serverName: 'Test', uptime: '0:00', uptimeSeconds: n, onlinePlayers: n,
  totalChars: 0, totalItems: 0, totalSectors: 0, tickCount: n, memoryMB: n * 2,
  accounts: 0, cpuPercent: n % 100, threadCount: 1,
  avgTickMs: 1, maxTickMs: 2, p50TickMs: 1, p95TickMs: n / 10, p99TickMs: 2,
  multicoreEnabled: false, maps: null,
  lastSaveUtc: null, lastSaveSeconds: 0, saveInProgress: false, lastSaveOk: null, saveCount: 0,
})

describe('stats history ring buffer', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('appends in arrival order, oldest first', () => {
    const server = useServerStore()
    for (const n of [1, 2, 3]) server.updateStats(stats(n))
    expect(server.history.map(s => s.players)).toEqual([1, 2, 3])
    expect(server.history[2]).toMatchObject({ memoryMB: 6, cpuPercent: 3, p95TickMs: 0.3 })
    expect(server.stats?.onlinePlayers).toBe(3)
  })

  it('caps the history and drops the oldest samples', () => {
    const server = useServerStore()
    const total = STATS_HISTORY_CAP + 25
    for (let n = 1; n <= total; n++) server.updateStats(stats(n))
    expect(server.history.length).toBe(STATS_HISTORY_CAP)
    expect(server.history[0].players).toBe(26)
    expect(server.history[STATS_HISTORY_CAP - 1].players).toBe(total)
    const order = server.history.map(s => s.players)
    expect(order).toEqual([...order].sort((a, b) => a - b))
  })

  it('pushSample returns a new array and leaves the input untouched', () => {
    const input = [1, 2, 3]
    const out = pushSample(input, 4, 3)
    expect(out).toEqual([2, 3, 4])
    expect(input).toEqual([1, 2, 3])
    expect(out).not.toBe(input)
    expect(pushSample([], 1, 3)).toEqual([1])
  })
})
