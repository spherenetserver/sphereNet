<template>
  <div>
    <div class="stats-grid">
      <StatCard label="Online Players" :value="stats?.onlinePlayers ?? '—'" :icon="Users"
        :sub="stats ? `${stats.accounts} accounts total` : undefined" />
      <StatCard label="Characters"     :value="fmt(stats?.totalChars)"      :icon="UserRound" />
      <StatCard label="Items"          :value="fmt(stats?.totalItems)"       :icon="Package" />
      <StatCard label="Memory"         :value="stats ? `${stats.memoryMB} MB` : '—'" :icon="MemoryStick" />
      <StatCard label="CPU"            :value="stats ? `${stats.cpuPercent} %` : '—'" :icon="Cpu" />
      <StatCard label="Uptime"         :value="stats?.uptime ?? '—'"         :icon="Clock" />
      <StatCard label="Tick Count"     :value="fmt(stats?.tickCount)"        :icon="Activity"
        :sub="stats ? `${stats.totalSectors} sectors` : undefined" />
      <StatCard label="Threads"        :value="fmt(stats?.threadCount)"     :icon="Layers" />
    </div>

    <template v-if="stats">
      <!-- Tick latency -->
      <section class="card">
        <div class="card-head">
          <h2 class="section-title">Tick Latency</h2>
          <span class="badge" :class="stats.multicoreEnabled ? 'on' : 'off'">
            {{ stats.multicoreEnabled ? 'Multicore' : 'Single-thread' }}
          </span>
        </div>
        <div class="latency-grid">
          <div v-for="c in latencyCells" :key="c.label" class="latency-cell">
            <span class="latency-label">{{ c.label }}</span>
            <span class="latency-value" :class="tickClass(c.value)">{{ fmtMs(c.value) }}</span>
          </div>
        </div>
      </section>

      <!-- Sparklines -->
      <section class="spark-grid">
        <div v-for="s in sparks" :key="s.label" class="spark-card">
          <div class="spark-head">
            <span class="spark-label">{{ s.label }}</span>
            <span class="spark-value">{{ s.current }}</span>
          </div>
          <Sparkline :values="s.values" :color="s.color" :label="`${s.label} history`" :zero-based="s.zeroBased" />
          <span class="spark-sub">{{ historySpan }}</span>
        </div>
      </section>

      <div class="two-col">
        <!-- Last save -->
        <section class="card">
          <div class="card-head">
            <h2 class="section-title">World Save</h2>
            <span v-if="stats.saveInProgress" class="badge warn">
              <Loader2 :size="11" class="spin" /> Saving…
            </span>
            <span v-else-if="stats.lastSaveOk === false" class="badge bad">Last save failed</span>
            <span v-else-if="stats.lastSaveOk === true" class="badge on">OK</span>
          </div>
          <div class="info-grid">
            <div class="info-row">
              <span class="info-key">Last save</span>
              <span class="info-val" :class="{ bad: stats.lastSaveOk === false }"
                :title="stats.lastSaveUtc ? new Date(stats.lastSaveUtc).toLocaleString() : undefined">
                {{ stats.lastSaveUtc ? timeAgo(stats.lastSaveUtc) : 'Not saved since start' }}
              </span>
            </div>
            <div class="info-row">
              <span class="info-key">Duration</span>
              <span class="info-val">{{ stats.lastSaveUtc ? `${(stats.lastSaveSeconds ?? 0).toFixed(2)} s` : '—' }}</span>
            </div>
            <div class="info-row">
              <span class="info-key">Saves this session</span>
              <span class="info-val">{{ stats.saveCount ?? 0 }}</span>
            </div>
          </div>
        </section>

        <!-- Server info -->
        <section class="card">
          <h2 class="section-title">Server Info</h2>
          <div class="info-grid">
            <div class="info-row">
              <span class="info-key">Server Name</span>
              <span class="info-val">{{ stats.serverName }}</span>
            </div>
            <div class="info-row">
              <span class="info-key">Uptime (seconds)</span>
              <span class="info-val">{{ stats.uptimeSeconds.toLocaleString() }}</span>
            </div>
            <div class="info-row">
              <span class="info-key">Online / Accounts</span>
              <span class="info-val">{{ stats.onlinePlayers }} / {{ stats.accounts }}</span>
            </div>
            <div class="info-row">
              <span class="info-key">Memory</span>
              <span class="info-val">{{ stats.memoryMB }} MB</span>
            </div>
          </div>
        </section>
      </div>

      <!-- Per-map -->
      <section v-if="stats.maps && stats.maps.length > 0" class="card">
        <h2 class="section-title">Maps</h2>
        <div class="table-scroll">
          <table class="table">
            <thead>
              <tr>
                <th>Map</th>
                <th class="num">Players</th>
                <th class="num">Chars</th>
                <th class="num">Items</th>
                <th class="num">Active / Sectors</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="m in stats.maps" :key="m.mapId">
                <td class="bold">{{ mapName(m.mapId) }} <span class="text-muted">#{{ m.mapId }}</span></td>
                <td class="num">{{ m.onlinePlayers.toLocaleString() }}</td>
                <td class="num">{{ m.chars.toLocaleString() }}</td>
                <td class="num">{{ m.items.toLocaleString() }}</td>
                <td class="num">{{ m.activeSectors.toLocaleString() }} / {{ m.sectors.toLocaleString() }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </section>

      <p v-if="!server.connected" class="poll-note">
        Live updates disconnected — refreshing every 5 s{{ fetchError ? ` (last attempt failed: ${fetchError})` : '' }}.
      </p>
    </template>

    <div v-else class="no-data">
      <template v-if="fetchError">
        <AlertTriangle :size="32" class="no-data-icon error-icon" />
        <p>Could not read server stats: {{ fetchError }}</p>
        <p class="sub">Retrying every 5 s while live updates are disconnected.</p>
      </template>
      <template v-else>
        <Activity :size="32" class="no-data-icon" />
        <p>Waiting for server stats…</p>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import {
  Users, UserRound, Package, MemoryStick, Clock, Activity, Cpu, Layers, Loader2, AlertTriangle,
} from 'lucide-vue-next'
import StatCard from '@/components/StatCard.vue'
import Sparkline from '@/components/Sparkline.vue'
import { useServerStore } from '@/stores/server'
import { serverApi, errorMessage } from '@/lib/api'

const server = useServerStore()
const stats  = computed(() => server.stats)

// --- REST fallback: one read on mount, then every 5 s while the hub is down ---
const fetchError = ref('')
let pollTimer: ReturnType<typeof setInterval> | null = null

async function fetchStatus() {
  try {
    const { data } = await serverApi.status()
    server.updateStats(data)
    fetchError.value = ''
  } catch (e) {
    fetchError.value = errorMessage(e)
  }
}

function stopPolling() {
  if (pollTimer) { clearInterval(pollTimer); pollTimer = null }
}

watch(() => server.connected, connected => {
  if (connected) {
    stopPolling()
    fetchError.value = ''
  } else if (!pollTimer) {
    pollTimer = setInterval(fetchStatus, 5000)
  }
}, { immediate: true })

// Ticks the "x ago" labels.
const now = ref(Date.now())
let clock: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  void fetchStatus()
  clock = setInterval(() => (now.value = Date.now()), 1000)
})

onUnmounted(() => {
  stopPolling()
  if (clock) clearInterval(clock)
})

// --- Tick latency ---
const latencyCells = computed(() => {
  const s = stats.value
  if (!s) return []
  return [
    { label: 'Avg', value: s.avgTickMs },
    { label: 'p50', value: s.p50TickMs },
    { label: 'p95', value: s.p95TickMs },
    { label: 'p99', value: s.p99TickMs },
    { label: 'Max', value: s.maxTickMs },
  ]
})

// Server tick is 100 ms; a tick that takes most of that is falling behind.
function tickClass(ms: number | undefined): string {
  if (ms === undefined || !Number.isFinite(ms)) return ''
  if (ms >= 100) return 'bad'
  if (ms >= 50) return 'warn'
  return ''
}

function fmtMs(ms: number | undefined): string {
  if (ms === undefined || !Number.isFinite(ms)) return '—'
  return ms >= 100 ? `${ms.toFixed(0)} ms` : `${ms.toFixed(2)} ms`
}

// --- Sparklines ---
const sparks = computed(() => {
  const h = server.history
  const last = h[h.length - 1]
  return [
    { label: 'Players', values: h.map(x => x.players), color: 'var(--accent)', zeroBased: true,
      current: last ? String(last.players) : '—' },
    { label: 'Memory', values: h.map(x => x.memoryMB), color: 'var(--success)', zeroBased: false,
      current: last ? `${last.memoryMB} MB` : '—' },
    { label: 'CPU', values: h.map(x => x.cpuPercent), color: 'var(--warning)', zeroBased: true,
      current: last ? `${last.cpuPercent} %` : '—' },
    { label: 'p95 Tick', values: h.map(x => x.p95TickMs), color: 'var(--danger)', zeroBased: true,
      current: last ? fmtMs(last.p95TickMs) : '—' },
  ]
})

const historySpan = computed(() => {
  const h = server.history
  if (h.length < 2) return 'Collecting samples…'
  const sec = Math.round((h[h.length - 1].t - h[0].t) / 1000)
  return `Last ${sec >= 60 ? `${Math.round(sec / 60)} min` : `${sec} s`} · ${h.length} samples`
})

// --- Formatting ---
function timeAgo(iso: string): string {
  const t = Date.parse(iso)
  if (Number.isNaN(t)) return '—'
  const sec = Math.max(0, Math.round((now.value - t) / 1000))
  if (sec < 60) return `${sec}s ago`
  const min = Math.floor(sec / 60)
  if (min < 60) return `${min}m ago`
  const h = Math.floor(min / 60)
  if (h < 48) return `${h}h ${min % 60}m ago`
  return `${Math.floor(h / 24)}d ago`
}

function fmt(n: number | undefined): string {
  return n === undefined ? '—' : n.toLocaleString()
}

function mapName(id: number): string {
  const names: Record<number, string> = { 0: 'Felucca', 1: 'Trammel', 2: 'Ilshenar', 3: 'Malas', 4: 'Tokuno', 5: 'TerMur' }
  return names[id] ?? `Map${id}`
}
</script>

<style scoped>
.stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}

.card {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 20px;
  margin-bottom: 24px;
}

.card-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.card-head .section-title { margin: 0; }

.section-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
  margin: 0 0 16px;
}

.badge {
  display: inline-flex; align-items: center; gap: 4px;
  padding: 2px 8px; border-radius: 9999px;
  font-size: 11px; font-weight: 600;
}

.badge.on   { background: rgba(63,185,80,0.15);   color: var(--success); }
.badge.off  { background: rgba(139,148,158,0.15); color: var(--text-muted); }
.badge.warn { background: rgba(210,153,34,0.15);  color: var(--warning); }
.badge.bad  { background: rgba(248,81,73,0.15);   color: var(--danger); }

.latency-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(120px, 1fr));
  gap: 12px;
}

.latency-cell {
  display: flex; flex-direction: column; gap: 6px;
  padding: 12px 14px; border-radius: 8px;
  background: var(--bg-tertiary);
}

.latency-label {
  font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted);
}

.latency-value { font-size: 20px; font-weight: 700; color: var(--text-primary); }
.latency-value.warn { color: var(--warning); }
.latency-value.bad  { color: var(--danger); }

.spark-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
  gap: 16px;
  margin-bottom: 24px;
}

.spark-card {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 16px 16px 12px;
  display: flex; flex-direction: column; gap: 8px;
}

.spark-head { display: flex; justify-content: space-between; align-items: baseline; }

.spark-label {
  font-size: 12px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted);
}

.spark-value { font-size: 16px; font-weight: 700; color: var(--text-primary); }
.spark-sub   { font-size: 11px; color: var(--text-muted); }

.two-col {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(320px, 1fr));
  gap: 0 24px;
}

.info-grid { display: flex; flex-direction: column; gap: 12px; }

.info-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  padding-bottom: 12px;
  border-bottom: 1px solid var(--border);
}

.info-row:last-child { border-bottom: none; padding-bottom: 0; }

.info-key {
  font-size: 13px;
  color: var(--text-muted);
}

.info-val {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-primary);
}

.info-val.bad { color: var(--danger); }

.table-scroll { overflow-x: auto; }

.table { width: 100%; border-collapse: collapse; font-size: 13px; }

.table thead th {
  text-align: left; padding: 10px 12px;
  font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted); border-bottom: 1px solid var(--border);
  white-space: nowrap;
}

.table tbody td { padding: 10px 12px; border-bottom: 1px solid var(--border); color: var(--text-primary); }
.table tbody tr:last-child td { border-bottom: none; }
.table .num { text-align: right; font-variant-numeric: tabular-nums; }

.bold { font-weight: 600; }
.text-muted { color: var(--text-muted); font-weight: 400; }

.poll-note { font-size: 12px; color: var(--warning); margin: 0; }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

.no-data {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 64px 0;
  color: var(--text-muted);
}

.no-data-icon { opacity: 0.4; }
.error-icon { color: var(--danger); opacity: 0.8; }

.no-data p { margin: 0; font-size: 14px; }
.no-data p.sub { font-size: 12px; }
</style>
