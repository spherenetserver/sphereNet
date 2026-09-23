<template>
  <div>
    <div class="toolbar">
      <div class="toolbar-left">
        <span class="count">{{ players.length }} online</span>
        <input v-model="search" class="search-input" placeholder="Search name, account, IP, serial…" />
      </div>
      <button class="btn-ghost" @click="refetch()">
        <RefreshCw :size="15" :class="{ spin: isFetching }" /> Refresh
      </button>
    </div>

    <div v-if="actionError" class="banner error">
      <span>{{ actionError }}</span>
      <button class="icon-btn plain" title="Dismiss" @click="actionError = ''"><X :size="14" /></button>
    </div>
    <div v-else-if="actionInfo" class="banner info">
      <span>{{ actionInfo }}</span>
      <button class="icon-btn plain" title="Dismiss" @click="actionInfo = ''"><X :size="14" /></button>
    </div>

    <div class="table-wrap">
      <table class="table" v-if="filtered.length > 0">
        <thead>
          <tr>
            <th>Character</th>
            <th>Serial</th>
            <th>Account</th>
            <th>PrivLevel</th>
            <th>Map</th>
            <th>Position</th>
            <th>IP</th>
            <th>Client</th>
            <th>Session</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in filtered" :key="p.serial">
            <td class="bold">{{ p.charName }}</td>
            <td class="mono text-muted">{{ hex(p.serial) }}</td>
            <td>{{ p.accountName }}</td>
            <td><span class="badge" :class="`plevel-${p.privLevel}`">{{ privLabel(p.privLevel) }}</span></td>
            <td>{{ mapName(p.mapId) }}</td>
            <td class="mono">{{ p.x }}, {{ p.y }}</td>
            <td class="mono text-muted">{{ p.ip }}</td>
            <td class="mono text-muted">{{ p.clientVersion || '—' }}</td>
            <td class="text-muted">{{ fmtSession(p.sessionSeconds) }}</td>
            <td class="actions">
              <button class="icon-btn" title="Send message" :disabled="busy.has(p.serial)" @click="openMessage(p)">
                <MessageSquare :size="15" />
              </button>
              <button class="icon-btn danger" title="Disconnect" :disabled="busy.has(p.serial)" @click="disconnect(p)">
                <Unplug :size="15" />
              </button>
            </td>
          </tr>
        </tbody>
      </table>

      <div v-else-if="isPending" class="empty">
        <Loader2 :size="32" class="empty-icon spin" />
        <p>Loading players…</p>
      </div>
      <div v-else-if="isError && players.length === 0" class="empty">
        <AlertTriangle :size="32" class="empty-icon error-icon" />
        <p>Could not load the player list: {{ loadError }}</p>
        <button class="btn-ghost" @click="refetch()">Retry</button>
      </div>
      <div v-else-if="players.length > 0" class="empty">
        <Search :size="32" class="empty-icon" />
        <p>No online player matches "{{ search.trim() }}".</p>
      </div>
      <div v-else class="empty">
        <Users :size="32" class="empty-icon" />
        <p>No players online.</p>
      </div>
    </div>
    <p v-if="isError && players.length > 0" class="stale-note">
      Refresh failed ({{ loadError }}); showing the last loaded list.
    </p>

    <!-- Message modal -->
    <div v-if="msgTarget" class="modal-overlay" @click.self="closeMessage">
      <div class="modal">
        <h3>Message {{ msgTarget.charName }}</h3>
        <div class="field">
          <label>Text</label>
          <input ref="msgInput" v-model="msgText" maxlength="500" placeholder="System message shown in game…"
            @keyup.enter="sendMessage" />
        </div>
        <p v-if="msgError" class="error-msg">{{ msgError }}</p>
        <div class="modal-actions">
          <button class="btn-ghost" @click="closeMessage">Cancel</button>
          <button class="btn-accent" :disabled="msgSending || !msgText.trim()" @click="sendMessage">
            <Send :size="14" /> Send
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, nextTick, ref } from 'vue'
import {
  RefreshCw, Users, Search, Loader2, AlertTriangle, MessageSquare, Unplug, Send, X,
} from 'lucide-vue-next'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { playersApi, errorMessage, type PlayerInfo } from '@/lib/api'

const qc = useQueryClient()
const { data, isPending, isError, error, isFetching, refetch } = useQuery({
  queryKey: ['players'],
  queryFn: () => playersApi.list().then(r => r.data),
  refetchInterval: 5000,
})

const players   = computed(() => data.value ?? [])
const loadError = computed(() => errorMessage(error.value))

const search   = ref('')
const filtered = computed(() => {
  const q = search.value.trim().toLowerCase()
  if (!q) return players.value
  return players.value.filter(p =>
    p.charName.toLowerCase().includes(q) ||
    p.accountName.toLowerCase().includes(q) ||
    p.ip.toLowerCase().includes(q) ||
    hex(p.serial).toLowerCase().includes(q) ||
    (p.clientVersion ?? '').toLowerCase().includes(q))
})

const actionError = ref('')
const actionInfo  = ref('')
const busy        = ref(new Set<number>())

async function disconnect(p: PlayerInfo) {
  if (!confirm(`Disconnect ${p.charName} (${p.accountName})?`)) return
  actionError.value = actionInfo.value = ''
  busy.value.add(p.serial)
  try {
    const { data } = await playersApi.disconnect(p.serial)
    actionInfo.value = data?.message ?? `${p.charName} disconnected.`
  } catch (e) {
    actionError.value = `Disconnect ${p.charName} failed: ${errorMessage(e)}`
  } finally {
    busy.value.delete(p.serial)
    qc.invalidateQueries({ queryKey: ['players'] })
  }
}

// --- Message modal ---
const msgTarget  = ref<PlayerInfo | null>(null)
const msgText    = ref('')
const msgError   = ref('')
const msgSending = ref(false)
const msgInput   = ref<HTMLInputElement | null>(null)

async function openMessage(p: PlayerInfo) {
  msgTarget.value = p
  msgText.value = ''
  msgError.value = ''
  await nextTick()
  msgInput.value?.focus()
}

function closeMessage() {
  msgTarget.value = null
}

async function sendMessage() {
  const p = msgTarget.value
  const text = msgText.value.trim()
  if (!p || !text) return
  msgError.value = ''
  msgSending.value = true
  try {
    await playersApi.message(p.serial, text)
    actionError.value = ''
    actionInfo.value = `Message sent to ${p.charName}.`
    closeMessage()
  } catch (e) {
    msgError.value = errorMessage(e, 'Send failed')
  } finally {
    msgSending.value = false
  }
}

// --- Formatting ---
const privLabels: Record<number, string> = {
  0: 'Guest', 1: 'Player', 2: 'Counselor', 3: 'Seer',
  4: 'GM', 5: 'Dev', 6: 'Admin', 7: 'Owner',
}

function privLabel(n: number): string { return privLabels[n] ?? `L${n}` }

function hex(serial: number): string {
  return '0x' + (serial >>> 0).toString(16).toUpperCase().padStart(8, '0')
}

function fmtSession(sec: number): string {
  if (!Number.isFinite(sec) || sec < 0) return '—'
  const s = Math.floor(sec)
  const h = Math.floor(s / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (h > 0) return `${h}h ${m}m`
  if (m > 0) return `${m}m ${s % 60}s`
  return `${s}s`
}

function mapName(id: number): string {
  const names: Record<number, string> = { 0: 'Felucca', 1: 'Trammel', 2: 'Ilshenar', 3: 'Malas', 4: 'Tokuno', 5: 'TerMur' }
  return names[id] ?? `Map${id}`
}
</script>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-bottom: 16px;
}

.toolbar-left { display: flex; align-items: center; gap: 14px; flex-wrap: wrap; }

.count {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-muted);
}

.search-input {
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 13px;
  padding: 7px 12px;
  outline: none;
  width: 260px;
}

.search-input:focus { border-color: var(--accent); }

.btn-ghost {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text-muted);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: all 0.15s;
}

.btn-ghost:hover { background: var(--bg-tertiary); color: var(--text-primary); }

.btn-accent {
  display: flex; align-items: center; gap: 6px;
  background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 7px 14px;
  cursor: pointer; transition: background 0.15s;
}

.btn-accent:hover:not(:disabled) { background: var(--accent-hover); }
.btn-accent:disabled { opacity: 0.5; cursor: not-allowed; }

.banner {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
  margin-bottom: 12px; padding: 8px 12px; border-radius: 6px; font-size: 13px;
}

.banner.error { background: rgba(248,81,73,0.1); border: 1px solid var(--danger); color: var(--danger); }
.banner.info  { background: rgba(63,185,80,0.1); border: 1px solid var(--success); color: var(--success); }

.spin { animation: spin 1s linear infinite; }

@keyframes spin { to { transform: rotate(360deg); } }

.table-wrap {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  overflow-x: auto;
}

.table {
  width: 100%;
  border-collapse: collapse;
  font-size: 13px;
}

.table thead th {
  text-align: left;
  padding: 12px 16px;
  font-size: 11px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--text-muted);
  border-bottom: 1px solid var(--border);
  white-space: nowrap;
}

.table tbody td {
  padding: 12px 16px;
  border-bottom: 1px solid var(--border);
  color: var(--text-primary);
  white-space: nowrap;
}

.table tbody tr:last-child td { border-bottom: none; }
.table tbody tr:hover { background: rgba(255,255,255,0.02); }

.bold { font-weight: 600; }
.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }

.badge {
  display: inline-block; padding: 2px 8px; border-radius: 9999px;
  font-size: 11px; font-weight: 600;
}

.plevel-0 { background: rgba(139,148,158,0.15); color: var(--text-muted); }
.plevel-1 { background: rgba(88,166,255,0.1);  color: var(--accent); }
.plevel-2, .plevel-3 { background: rgba(63,185,80,0.12); color: var(--success); }
.plevel-4, .plevel-5, .plevel-6, .plevel-7 { background: rgba(210,153,34,0.15); color: var(--warning); }

.actions { display: flex; gap: 6px; justify-content: flex-end; }

.icon-btn {
  display: flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); cursor: pointer; transition: all 0.15s;
}

.icon-btn:hover:not(:disabled) { background: var(--bg-tertiary); color: var(--text-primary); }
.icon-btn.danger:hover:not(:disabled) { border-color: var(--danger); color: var(--danger); }
.icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }
.icon-btn.plain { width: 24px; height: 24px; border-color: transparent; color: inherit; }

.empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 12px;
  padding: 64px;
  color: var(--text-muted);
}

.empty-icon { opacity: 0.3; }
.error-icon { color: var(--danger); opacity: 0.8; }
.empty p { margin: 0; font-size: 14px; }

.stale-note { font-size: 13px; color: var(--danger); margin: 8px 0 0; }

.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.6);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}

.modal {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 12px; padding: 28px; width: 420px; max-width: calc(100vw - 32px);
  display: flex; flex-direction: column; gap: 16px;
}

.modal h3 { margin: 0; font-size: 16px; font-weight: 600; }

.field { display: flex; flex-direction: column; gap: 6px; }
.field label { font-size: 12px; font-weight: 500; color: var(--text-muted); }
.field input {
  background: var(--bg-tertiary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 14px; padding: 9px 12px; outline: none;
}
.field input:focus { border-color: var(--accent); }

.error-msg { font-size: 13px; color: var(--danger); margin: 0; }

.modal-actions { display: flex; justify-content: flex-end; gap: 8px; }
</style>
