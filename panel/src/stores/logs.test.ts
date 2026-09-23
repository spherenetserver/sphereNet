import { createPinia, setActivePinia } from 'pinia'
import { nextTick } from 'vue'
import { beforeEach, describe, expect, it } from 'vitest'
import { useLogsStore } from './logs'
import { retryPolicy } from '@/lib/signalr'

const line = (message: string) => ({
  timestamp: '2026-09-23T10:00:00Z', level: 'Information', message, source: 'Server',
})

describe('console log store', () => {
  beforeEach(() => setActivePinia(createPinia()))

  it('keeps counting lines after the buffer is full', () => {
    const logs = useLogsStore()
    logs.addBatch(Array.from({ length: 2000 }, (_, i) => line(`a${i}`)))
    const before = logs.received
    logs.addBatch([line('next')])

    // entries.length stays at the cap; received is what the view follows.
    expect(logs.entries.length).toBe(2000)
    expect(logs.received).toBe(before + 1)
    expect(logs.entries[logs.entries.length - 1].message).toBe('next')
  })

  it('holds lines while paused and shows them on resume', async () => {
    const logs = useLogsStore()
    logs.addEntry(line('before'))
    logs.paused = true
    logs.addBatch([line('during-1'), line('during-2')])
    expect(logs.entries.map(e => e.message)).toEqual(['before'])
    expect(logs.held).toBe(2)

    logs.paused = false
    await nextTick()
    expect(logs.entries.map(e => e.message)).toEqual(['before', 'during-1', 'during-2'])
    expect(logs.held).toBe(0)
  })

  it('gives every line a distinct id', () => {
    const logs = useLogsStore()
    logs.addBatch([line('x'), line('x')])
    const [a, b] = logs.entries
    expect(a.id).not.toBe(b.id)
  })
})

describe('hub reconnect policy', () => {
  it('never gives up', () => {
    const delay = (n: number) => retryPolicy.nextRetryDelayInMilliseconds({
      previousRetryCount: n, elapsedMilliseconds: n * 15_000, retryReason: new Error('x'),
    })
    expect(delay(0)).toBe(0)
    // The library default returns null after the 4th try, which stops reconnecting.
    for (const n of [4, 10, 100]) expect(delay(n)).toBe(15_000)
  })
})
