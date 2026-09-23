import { defineStore } from 'pinia'
import { ref, shallowRef } from 'vue'
import type { ServerStats, PlayerInfo } from '@/lib/api'

/** One sample of the dashboard sparklines, taken from each stats update. */
export interface StatsSample {
  /** Client-side receive time (ms since epoch). */
  t: number
  players: number
  memoryMB: number
  cpuPercent: number
  p95TickMs: number
}

/** ~5 minutes at the hub's 2 s push interval. */
export const STATS_HISTORY_CAP = 150

/** Appends a sample, dropping the oldest ones past `cap`. Returns a new array
 *  (oldest first) so a shallowRef sees the change. */
export function pushSample<T>(history: readonly T[], sample: T, cap = STATS_HISTORY_CAP): T[] {
  const next = history.length >= cap ? history.slice(history.length - cap + 1) : history.slice()
  next.push(sample)
  return next
}

export const useServerStore = defineStore('server', () => {
  const stats = ref<ServerStats | null>(null)
  const players = ref<PlayerInfo[]>([])
  const connected = ref(false)
  // Replaced wholesale on each append; no need for deep reactivity on 150 rows.
  const history = shallowRef<StatsSample[]>([])

  function updateStats(s: ServerStats) {
    stats.value = s
    history.value = pushSample(history.value, {
      t: Date.now(),
      players: s.onlinePlayers ?? 0,
      memoryMB: s.memoryMB ?? 0,
      cpuPercent: s.cpuPercent ?? 0,
      p95TickMs: s.p95TickMs ?? 0,
    })
  }

  function updatePlayers(p: PlayerInfo[]) {
    players.value = p
  }

  function setConnected(v: boolean) {
    connected.value = v
  }

  return { stats, players, connected, history, updateStats, updatePlayers, setConnected }
})
