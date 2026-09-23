<template>
  <div>
    <h1 class="page-title">{{ t('settings.title') }}</h1>

    <!-- Debugging -->
    <section class="card">
      <div class="card-header">
        <Bug :size="16" />
        <h2>{{ t('settings.debugging') }}</h2>
      </div>

      <div v-if="debugLoading" class="loading">{{ t('common.loading') }}</div>

      <div v-else class="toggle-list">
        <div class="toggle-row">
          <div class="toggle-info">
            <span class="toggle-label">{{ t('settings.packetDebug') }}</span>
            <span class="toggle-desc">{{ t('settings.packetDebugDesc') }}</span>
          </div>
          <button
            class="toggle-btn"
            :class="{ on: debugState.packetDebug }"
            @click="togglePacket"
            :disabled="debugSaving"
          >
            {{ debugState.packetDebug ? t('common.on') : t('common.off') }}
          </button>
        </div>

        <div class="toggle-row">
          <div class="toggle-info">
            <span class="toggle-label">{{ t('settings.scriptDebug') }}</span>
            <span class="toggle-desc">{{ t('settings.scriptDebugDesc') }}</span>
          </div>
          <button
            class="toggle-btn"
            :class="{ on: debugState.scriptDebug }"
            @click="toggleScript"
            :disabled="debugSaving"
          >
            {{ debugState.scriptDebug ? t('common.on') : t('common.off') }}
          </button>
        </div>
      </div>

      <p v-if="debugError" class="error-msg">{{ debugError }}</p>
      <p v-if="debugSaved" class="success-msg">{{ t('settings.saved') }}</p>
    </section>

    <!-- Server Configuration -->
    <section class="card">
      <div class="card-header">
        <Settings :size="16" />
        <h2>{{ t('settings.configuration') }}</h2>
      </div>
      <p class="card-desc">
        <I18nT k="settings.configText">
          <template #link><RouterLink to="/setup" class="link">{{ t('settings.setupWizard') }}</RouterLink></template>
        </I18nT>
      </p>
    </section>

    <!-- Danger Zone -->
    <section class="card danger-card">
      <div class="card-header">
        <AlertTriangle :size="16" />
        <h2>{{ t('settings.dangerZone') }}</h2>
      </div>

      <div class="danger-actions">
        <div class="danger-row">
          <div class="danger-info">
            <span class="danger-label">{{ t('settings.restartServer') }}</span>
            <span class="danger-desc">{{ t('settings.restartDesc') }}</span>
          </div>
          <button class="btn-danger" @click="doRestart" :disabled="actionBusy">{{ t('settings.restart') }}</button>
        </div>

        <div class="danger-row">
          <div class="danger-info">
            <span class="danger-label">{{ t('settings.shutdownServer') }}</span>
            <span class="danger-desc">{{ t('settings.shutdownDesc') }}</span>
          </div>
          <button class="btn-danger" @click="doShutdown" :disabled="actionBusy">{{ t('settings.shutdown') }}</button>
        </div>
      </div>

      <p v-if="actionMsg" class="success-msg">{{ actionMsg }}</p>
    </section>
  </div>
</template>

<script setup lang="ts">
import { ref, onMounted } from 'vue'
import { RouterLink } from 'vue-router'
import { Bug, Settings, AlertTriangle } from 'lucide-vue-next'
import { settingsApi, serverApi } from '@/lib/api'
import type { DebugState } from '@/lib/api'
import { t } from '@/i18n'
import I18nT from '@/i18n/I18nT'

const debugLoading = ref(true)
const debugSaving  = ref(false)
const debugSaved   = ref(false)
const debugError   = ref('')
const actionBusy   = ref(false)
const actionMsg    = ref('')

const debugState = ref<DebugState>({ packetDebug: false, scriptDebug: false })

onMounted(async () => {
  try {
    const { data } = await settingsApi.getDebug()
    debugState.value = data
  } catch {
    debugError.value = t('settings.loadDebugFailed')
  } finally {
    debugLoading.value = false
  }
})

async function saveDebug() {
  debugSaving.value = true
  debugSaved.value  = false
  debugError.value  = ''
  try {
    await settingsApi.setDebug(debugState.value)
    debugSaved.value = true
    setTimeout(() => { debugSaved.value = false }, 3000)
  } catch {
    debugError.value = t('settings.saveDebugFailed')
  } finally {
    debugSaving.value = false
  }
}

async function togglePacket() {
  debugState.value.packetDebug = !debugState.value.packetDebug
  await saveDebug()
}

async function toggleScript() {
  debugState.value.scriptDebug = !debugState.value.scriptDebug
  await saveDebug()
}

async function doRestart() {
  if (!confirm(t('settings.confirmRestart'))) return
  actionBusy.value = true
  actionMsg.value  = ''
  try {
    await serverApi.restart()
    actionMsg.value = t('settings.restartInitiated')
  } catch {
    actionMsg.value = t('settings.restartFailed')
  } finally {
    actionBusy.value = false
  }
}

async function doShutdown() {
  if (!confirm(t('settings.confirmShutdown'))) return
  actionBusy.value = true
  actionMsg.value  = ''
  try {
    await serverApi.shutdown()
    actionMsg.value = t('settings.shutdownInitiated')
  } catch {
    actionMsg.value = t('settings.shutdownFailed')
  } finally {
    actionBusy.value = false
  }
}
</script>

<style scoped>
.page-title {
  font-size: 20px;
  font-weight: 700;
  color: var(--text-primary);
  margin: 0 0 20px;
}

.card {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  padding: 20px;
  margin-bottom: 16px;
}

.card-header {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 16px;
  color: var(--text-muted);
}

.card-header h2 {
  font-size: 14px;
  font-weight: 600;
  color: var(--text-primary);
  margin: 0;
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.card-desc { font-size: 13px; color: var(--text-muted); margin: 0; }

.link { color: var(--accent); text-decoration: none; }
.link:hover { text-decoration: underline; }

.loading { font-size: 13px; color: var(--text-muted); }

/* Toggles */
.toggle-list { display: flex; flex-direction: column; gap: 1px; }

.toggle-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 14px 0;
  border-bottom: 1px solid var(--border);
}

.toggle-row:last-child { border-bottom: none; }

.toggle-info { display: flex; flex-direction: column; gap: 3px; }
.toggle-label { font-size: 14px; font-weight: 500; color: var(--text-primary); }
.toggle-desc { font-size: 12px; color: var(--text-muted); }

.toggle-btn {
  min-width: 54px;
  padding: 6px 12px;
  border-radius: 20px;
  border: 2px solid var(--border);
  background: var(--bg-primary);
  color: var(--text-muted);
  font-size: 12px;
  font-weight: 700;
  cursor: pointer;
  transition: all 0.2s;
  letter-spacing: 0.05em;
}

.toggle-btn.on {
  border-color: var(--accent);
  background: var(--accent);
  color: #fff;
}

.toggle-btn:disabled { opacity: 0.5; cursor: not-allowed; }

/* Danger */
.danger-card { border-color: rgba(248, 81, 73, 0.3); }

.danger-actions { display: flex; flex-direction: column; gap: 1px; }

.danger-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 14px 0;
  border-bottom: 1px solid var(--border);
}

.danger-row:last-child { border-bottom: none; }

.danger-info { display: flex; flex-direction: column; gap: 3px; }
.danger-label { font-size: 14px; font-weight: 500; color: var(--text-primary); }
.danger-desc { font-size: 12px; color: var(--text-muted); }

.btn-danger {
  padding: 7px 16px;
  border-radius: 6px;
  border: 1px solid var(--danger);
  background: transparent;
  color: var(--danger);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  transition: all 0.15s;
}

.btn-danger:hover:not(:disabled) { background: var(--danger); color: #fff; }
.btn-danger:disabled { opacity: 0.5; cursor: not-allowed; }

.error-msg   { margin-top: 10px; font-size: 13px; color: var(--danger); }
.success-msg { margin-top: 10px; font-size: 13px; color: var(--success, #3fb950); }
</style>
