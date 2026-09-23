import { defineStore } from 'pinia'
import { ref, watch } from 'vue'
import type { LogEntry } from '@/lib/signalr'

const MAX_LOGS = 2000

/** A log line as the console keeps it: the entry plus a stable id for v-for keys. */
export interface ConsoleEntry extends LogEntry {
  id: number
}

export const useLogsStore = defineStore('logs', () => {
  const entries  = ref<ConsoleEntry[]>([])
  const paused   = ref(false)
  /** Lines appended so far. Grows forever - unlike entries.length, which stops
   *  changing once the buffer is full, so a watcher on the length went quiet. */
  const received = ref(0)
  /** Lines that arrived while paused, shown on resume (at most MAX_LOGS). */
  const held     = ref(0)

  let nextId = 1
  let pending: LogEntry[] = []

  function append(batch: LogEntry[]) {
    for (const entry of batch) entries.value.push({ ...entry, id: nextId++ })
    if (entries.value.length > MAX_LOGS) {
      entries.value.splice(0, entries.value.length - MAX_LOGS)
    }
    received.value += batch.length
  }

  function addBatch(batch: LogEntry[]) {
    if (batch.length === 0) return
    if (paused.value) {
      // Pausing is for reading, not for losing what comes in meanwhile.
      pending.push(...batch)
      if (pending.length > MAX_LOGS) pending.splice(0, pending.length - MAX_LOGS)
      held.value = pending.length
      return
    }
    append(batch)
  }

  function addEntry(entry: LogEntry) {
    addBatch([entry])
  }

  // Synchronous, so a pause/resume inside one tick still releases what it held.
  watch(paused, now => {
    if (now || pending.length === 0) return
    const batch = pending
    pending = []
    held.value = 0
    append(batch)
  }, { flush: 'sync' })

  function clear() {
    entries.value = []
    pending = []
    held.value = 0
  }

  return { entries, paused, received, held, addEntry, addBatch, clear }
})
