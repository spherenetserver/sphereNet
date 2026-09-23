<template>
  <header class="topbar">
    <h1 class="page-title">{{ title }}</h1>
    <div class="topbar-right">
      <!-- Game server status + controls -->
      <div class="server-ctrl">
        <span class="status-badge" :class="gameRunning ? 'running' : 'stopped'">
          <span class="dot" />
          {{ gameRunning ? t('topbar.serverRunning') : t('topbar.serverStopped') }}
        </span>
        <button v-if="!gameRunning" class="ctrl-btn start" @click="startServer" :disabled="busy">
          <Play :size="12" /> {{ t('topbar.start') }}
        </button>
        <button v-if="gameRunning" class="ctrl-btn restart" @click="restartServer" :disabled="busy">
          <RotateCw :size="12" /> {{ t('topbar.restart') }}
        </button>
        <button v-if="gameRunning" class="ctrl-btn stop" @click="stopServer" :disabled="busy">
          <Square :size="12" /> {{ t('topbar.stop') }}
        </button>
        <span v-if="ctrlError" class="ctrl-error" :title="ctrlError">
          {{ ctrlError }}
          <button class="ctrl-error-close" :title="t('common.dismiss')" @click="ctrlError = ''">&times;</button>
        </span>
      </div>

      <!-- Panel SignalR connection -->
      <span class="connection-badge" :class="server.connected ? 'online' : 'offline'">
        <span class="dot" />
        {{ server.connected ? t('topbar.panelConnected') : t('topbar.panelDisconnected') }}
      </span>
      <span class="server-name">{{ auth.serverName }}</span>
      <LanguageSwitch />
    </div>
  </header>
</template>

<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import { useRoute } from 'vue-router'
import { Play, Square, RotateCw } from 'lucide-vue-next'
import { useAuthStore } from '@/stores/auth'
import { useServerStore } from '@/stores/server'
import { serverApi, errorMessage } from '@/lib/api'
import { t, type MessageKey } from '@/i18n'
import LanguageSwitch from '@/components/LanguageSwitch.vue'

const auth   = useAuthStore()
const server = useServerStore()
const route  = useRoute()

const busy        = ref(false)
const ctrlError   = ref('')
const gameRunning = ref(true) // assume running until first poll
let   pollTimer: ReturnType<typeof setInterval> | null = null

const titleMap: Record<string, MessageKey> = {
  dashboard: 'nav.dashboard',
  logs:      'nav.console',
  players:   'nav.players',
  accounts:  'nav.accounts',
  server:    'nav.server',
  scripts:   'nav.scripts',
  gumps:     'nav.gumpDesigner',
  updates:   'nav.updates',
  settings:  'nav.settings',
}

const title = computed(() => {
  const key = titleMap[route.name as string]
  return key ? t(key) : 'SphereNet'
})

async function pollRunning() {
  try {
    const { data } = await serverApi.running()
    gameRunning.value = data.running
  } catch { /* ignore */ }
}

onMounted(() => {
  pollRunning()
  pollTimer = setInterval(pollRunning, 5000)
})

onUnmounted(() => {
  if (pollTimer) clearInterval(pollTimer)
})

async function control(what: string, call: () => Promise<unknown>, recheckMs: number) {
  busy.value = true
  ctrlError.value = ''
  try {
    await call()
  } catch (e) {
    ctrlError.value = t('topbar.actionFailed', { action: what, error: errorMessage(e) })
  } finally {
    busy.value = false
  }
  setTimeout(pollRunning, recheckMs)
}

function startServer() {
  return control(t('topbar.start'), serverApi.startServer, 3000)
}

function restartServer() {
  if (!confirm(t('topbar.confirmRestart'))) return
  return control(t('topbar.restart'), serverApi.restart, 5000)
}

function stopServer() {
  if (!confirm(t('topbar.confirmStop'))) return
  return control(t('topbar.stop'), serverApi.shutdown, 3000)
}
</script>

<style scoped>
.topbar {
  height: 56px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 24px;
  background: var(--bg-secondary);
  border-bottom: 1px solid var(--border);
}

.page-title {
  font-size: 16px;
  font-weight: 600;
  margin: 0;
  color: var(--text-primary);
}

.topbar-right {
  display: flex;
  align-items: center;
  gap: 12px;
}

/* Server status + controls */
.server-ctrl {
  display: flex;
  align-items: center;
  gap: 6px;
}

.status-badge {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 500;
  padding: 4px 10px;
  border-radius: 9999px;
}

.status-badge.running { background: rgba(63, 185, 80, 0.15);  color: var(--success, #3fb950); }
.status-badge.stopped { background: rgba(248, 81, 73, 0.15);  color: var(--danger,  #f85149); }

.ctrl-btn {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 10px;
  border-radius: 6px;
  border: 1px solid;
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  transition: all 0.15s;
}

.ctrl-btn:disabled { opacity: 0.5; cursor: not-allowed; }

.ctrl-btn.start   { border-color: var(--success, #3fb950); color: var(--success, #3fb950); background: transparent; }
.ctrl-btn.restart { border-color: var(--accent); color: var(--accent); background: transparent; }
.ctrl-btn.stop    { border-color: var(--danger, #f85149); color: var(--danger, #f85149); background: transparent; }

.ctrl-btn.start:not(:disabled):hover   { background: rgba(63, 185, 80, 0.15); }
.ctrl-btn.restart:not(:disabled):hover { background: rgba(88, 166, 255, 0.1); }
.ctrl-btn.stop:not(:disabled):hover    { background: rgba(248, 81, 73, 0.1); }

.ctrl-error {
  display: flex;
  align-items: center;
  gap: 4px;
  max-width: 280px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: var(--danger, #f85149);
}

.ctrl-error-close {
  border: none;
  background: transparent;
  color: inherit;
  font-size: 14px;
  line-height: 1;
  cursor: pointer;
  padding: 0 2px;
}

/* Panel connection badge */
.connection-badge {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  font-weight: 500;
  padding: 4px 10px;
  border-radius: 9999px;
}

.connection-badge.online  { background: rgba(63, 185, 80, 0.15);  color: var(--success, #3fb950); }
.connection-badge.offline { background: rgba(248, 81, 73, 0.15);  color: var(--danger,  #f85149); }

.dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: currentColor;
}

.server-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--text-muted);
}
</style>
