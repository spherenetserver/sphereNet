<template>
  <div class="server-page">

    <!-- Quick Actions -->
    <section class="section">
      <h2 class="section-title">{{ t('server.quickActions') }}</h2>
      <div class="actions-grid">
        <button class="action-card" @click="action('save')" :disabled="busy.save">
          <Save :size="22" class="action-icon success" />
          <span class="action-label">{{ t('server.saveWorld') }}</span>
          <span class="action-sub">{{ t('server.saveWorldSub') }}</span>
        </button>
        <button class="action-card" @click="action('resync')" :disabled="busy.resync">
          <RefreshCw :size="22" class="action-icon accent" :class="{ spin: busy.resync }" />
          <span class="action-label">{{ t('server.resync') }}</span>
          <span class="action-sub">{{ t('server.resyncSub') }}</span>
        </button>
        <button class="action-card" @click="action('respawn')" :disabled="busy.respawn">
          <Skull :size="22" class="action-icon warning" />
          <span class="action-label">{{ t('server.respawn') }}</span>
          <span class="action-sub">{{ t('server.respawnSub') }}</span>
        </button>
        <button class="action-card" @click="action('restock')" :disabled="busy.restock">
          <ShoppingBag :size="22" class="action-icon accent" />
          <span class="action-label">{{ t('server.restock') }}</span>
          <span class="action-sub">{{ t('server.restockSub') }}</span>
        </button>
        <button class="action-card" @click="action('gc')" :disabled="busy.gc">
          <Trash2 :size="22" class="action-icon muted" />
          <span class="action-label">{{ t('server.gc') }}</span>
          <span class="action-sub">{{ t('server.gcSub') }}</span>
        </button>
        <button class="action-card danger" @click="confirmShutdown" :disabled="busy.shutdown">
          <PowerOff :size="22" class="action-icon danger" />
          <span class="action-label">{{ t('server.shutdown') }}</span>
          <span class="action-sub">{{ t('server.shutdownSub') }}</span>
        </button>
      </div>
    </section>

    <!-- Broadcast -->
    <section class="section">
      <h2 class="section-title">{{ t('server.broadcastTitle') }}</h2>
      <div class="broadcast-row">
        <input v-model="broadcastMsg" class="broadcast-input" :placeholder="t('server.broadcastPlaceholder')" @keyup.enter="sendBroadcast" />
        <button class="btn-accent" @click="sendBroadcast" :disabled="!broadcastMsg.trim() || broadcasting">
          <Megaphone :size="15" /> {{ t('server.broadcast') }}
        </button>
      </div>
      <p v-if="broadcastSent" class="sent-msg">{{ t('server.broadcastSent') }}</p>
      <p v-if="broadcastError" class="error-msg">{{ broadcastError }}</p>
    </section>

    <!-- Scheduled shutdown / restart -->
    <section class="section">
      <h2 class="section-title">{{ t('server.scheduleTitle') }}</h2>
      <div class="panel-box">
        <div v-if="schedule?.pending" class="pending-row">
          <div class="pending-info">
            <Timer :size="20" class="action-icon warning" />
            <div>
              <div class="pending-title">
                <I18nT :k="schedule.restart ? 'server.restartIn' : 'server.shutdownIn'">
                  <template #time><span class="countdown">{{ countdown }}</span></template>
                </I18nT>
              </div>
              <div class="pending-sub" v-if="schedule.dueUtc">
                {{ t('server.dueAt', { time: fmtTime(schedule.dueUtc) }) }}
              </div>
            </div>
          </div>
          <button class="btn-danger" :disabled="scheduleBusy" @click="cancelSchedule">
            <X :size="14" /> {{ t('common.cancel') }}
          </button>
        </div>

        <template v-else>
          <div class="sched-row">
            <div class="seg">
              <button :class="{ active: schedRestart }" @click="schedRestart = true">
                <RotateCw :size="13" /> {{ t('server.restart') }}
              </button>
              <button :class="{ active: !schedRestart }" @click="schedRestart = false">
                <PowerOff :size="13" /> {{ t('server.shutdownShort') }}
              </button>
            </div>
            <div class="seg">
              <button v-for="m in delayPresets" :key="m" :class="{ active: schedMinutes === m }"
                @click="schedMinutes = m">{{ t('server.minutes', { n: m }) }}</button>
            </div>
          </div>
          <div class="broadcast-row">
            <input v-model="schedMessage" class="broadcast-input" maxlength="200"
              :placeholder="t('server.scheduleMessagePlaceholder')" />
            <button class="btn-accent" :disabled="scheduleBusy || schedule === undefined" @click="scheduleShutdown">
              <Timer :size="15" /> {{ schedRestart ? t('server.scheduleRestart') : t('server.scheduleShutdown') }}
            </button>
          </div>
        </template>

        <p v-if="scheduleError" class="error-msg">{{ scheduleError }}</p>
        <p v-if="schedulePollError" class="error-msg">{{ schedulePollError }}</p>
      </div>
    </section>

    <!-- IP blocks -->
    <section class="section">
      <h2 class="section-title">{{ t('server.ipTitle') }}</h2>
      <div class="panel-box">
        <p class="note">
          <Info :size="13" /> {{ t('server.ipNote') }}
        </p>
        <div class="broadcast-row">
          <input v-model="newIp" class="broadcast-input mono" :placeholder="t('server.ipPlaceholder')"
            @keyup.enter="addIp" />
          <button class="btn-accent" :disabled="!newIp.trim() || ipBusy" @click="addIp">
            <Ban :size="15" /> {{ t('server.block') }}
          </button>
        </div>
        <p v-if="ipError" class="error-msg">{{ ipError }}</p>
        <div v-if="ipLoading" class="ip-empty">{{ t('common.loading') }}</div>
        <div v-else-if="ipLoadError" class="ip-empty error-msg">{{ t('server.ipLoadError', { error: ipLoadError }) }}</div>
        <div v-else-if="ipBlocks.length === 0" class="ip-empty">{{ t('server.noBlocked') }}</div>
        <ul v-else class="ip-list">
          <li v-for="ip in ipBlocks" :key="ip">
            <span class="mono">{{ ip }}</span>
            <button class="icon-btn danger" :title="t('server.unblock')" :disabled="ipBusy" @click="removeIp(ip)">
              <Trash2 :size="14" />
            </button>
          </li>
        </ul>
      </div>
    </section>

    <!-- Console -->
    <section class="section">
      <h2 class="section-title">{{ t('server.console') }}</h2>
      <div class="console-box">
        <div ref="consoleEl" class="console-output">
          <div v-for="(line, i) in consoleLines" :key="i" class="console-line">
            <span class="console-prompt" v-if="line.type === 'cmd'">» </span>
            <span :class="line.type === 'cmd' ? 'console-cmd' : 'console-resp'">{{ line.text }}</span>
          </div>
          <div v-if="consoleLines.length === 0" class="console-empty">{{ t('server.consoleEmpty') }}</div>
        </div>
        <div class="console-input-row">
          <span class="prompt-label">»</span>
          <input
            v-model="cmdText"
            class="console-input"
            :placeholder="t('server.consolePlaceholder')"
            @keyup.enter="runCommand"
            @keyup.up="historyUp"
            @keyup.down="historyDown"
          />
        </div>
      </div>
    </section>

    <p v-if="feedback" class="feedback">{{ feedback }}</p>
  </div>
</template>

<script setup lang="ts">
import { ref, computed, nextTick, onMounted, onUnmounted } from 'vue'
import {
  Save, RefreshCw, Skull, ShoppingBag, Trash2, PowerOff, Megaphone, Timer, RotateCw, X, Info, Ban,
} from 'lucide-vue-next'
import { serverApi, ipBlocksApi, errorMessage, type ShutdownSchedule } from '@/lib/api'
import { isValidIp } from '@/lib/ip'
import { t, fmtTime, fmtNumber } from '@/i18n'
import I18nT from '@/i18n/I18nT'
import { executeCommand } from '@/lib/signalr'

const broadcastMsg   = ref('')
const broadcastSent  = ref(false)
const broadcastError = ref('')
const broadcasting   = ref(false)
const feedback       = ref('')

// busy flags per action
const busy = ref({ save: false, resync: false, respawn: false, restock: false, gc: false, shutdown: false })

type ActionKey = keyof typeof busy.value

async function action(key: ActionKey) {
  busy.value[key] = true
  feedback.value  = ''
  try {
    const res = await ({
      save:     serverApi.save,
      resync:   serverApi.resync,
      respawn:  serverApi.respawn,
      restock:  serverApi.restock,
      gc:       serverApi.gc,
      shutdown: serverApi.shutdown,
    } as Record<ActionKey, () => Promise<{ data: { message?: string; memoryMB?: number } }>>)[key]()
    feedback.value = res.data.message ?? (res.data.memoryMB ? t('server.gcDone', { mb: fmtNumber(res.data.memoryMB) }) : t('common.done'))
  } catch {
    feedback.value = t('server.actionFailed')
  } finally {
    busy.value[key] = false
  }
}

function confirmShutdown() {
  if (confirm(t('server.confirmShutdown'))) action('shutdown')
}

async function sendBroadcast() {
  if (!broadcastMsg.value.trim() || broadcasting.value) return
  broadcastError.value = ''
  broadcasting.value = true
  try {
    await serverApi.broadcast(broadcastMsg.value.trim())
    broadcastMsg.value  = ''
    broadcastSent.value = true
    setTimeout(() => broadcastSent.value = false, 3000)
  } catch (e) {
    broadcastError.value = errorMessage(e, t('server.broadcastFailed'))
  } finally {
    broadcasting.value = false
  }
}

// --- Scheduled shutdown / restart ---
const delayPresets  = [1, 5, 10, 15]
// undefined until the first GET answers, so the form can't race a pending one.
const schedule      = ref<ShutdownSchedule | undefined>(undefined)
const schedRestart  = ref(true)
const schedMinutes  = ref(5)
const schedMessage  = ref('')
const scheduleBusy  = ref(false)
const scheduleError = ref('')
const schedulePollError = ref('')
const now           = ref(Date.now())

const countdown = computed(() => {
  const due = schedule.value?.dueUtc ? Date.parse(schedule.value.dueUtc) : NaN
  if (Number.isNaN(due)) return '—'
  const sec = Math.max(0, Math.ceil((due - now.value) / 1000))
  const m = Math.floor(sec / 60)
  return `${m}:${String(sec % 60).padStart(2, '0')}`
})

async function loadSchedule() {
  try {
    const { data } = await serverApi.getSchedule()
    schedule.value = data
    schedulePollError.value = ''
  } catch (e) {
    // Keep the last known state; a failed poll alone shouldn't hide a pending shutdown.
    if (schedule.value === undefined) schedule.value = { pending: false, restart: false, dueUtc: null }
    schedulePollError.value = errorMessage(e, t('server.scheduleReadFailed'))
  }
}

async function scheduleShutdown() {
  const question = schedRestart.value ? 'server.confirmScheduleRestart' : 'server.confirmScheduleShutdown'
  if (!confirm(t(question, { n: schedMinutes.value }))) return
  scheduleBusy.value = true
  scheduleError.value = ''
  try {
    const { data } = await serverApi.schedule(
      schedMinutes.value * 60, schedRestart.value, schedMessage.value.trim() || undefined)
    schedule.value = data
    schedMessage.value = ''
  } catch (e) {
    const status = (e as { response?: { status?: number } }).response?.status
    scheduleError.value = status === 409
      ? t('server.alreadyScheduled')
      : errorMessage(e, t('server.scheduleFailed'))
    await loadSchedule()
  } finally {
    scheduleBusy.value = false
  }
}

async function cancelSchedule() {
  if (!confirm(t('server.confirmCancel'))) return
  scheduleBusy.value = true
  scheduleError.value = ''
  try {
    const { data } = await serverApi.cancelSchedule()
    schedule.value = data
  } catch (e) {
    scheduleError.value = errorMessage(e, t('server.cancelFailed'))
    await loadSchedule()
  } finally {
    scheduleBusy.value = false
  }
}

// --- IP blocks ---
const ipBlocks    = ref<string[]>([])
const ipLoading   = ref(true)
const ipLoadError = ref('')
const ipError     = ref('')
const ipBusy      = ref(false)
const newIp       = ref('')

async function loadIpBlocks() {
  try {
    const { data } = await ipBlocksApi.list()
    ipBlocks.value = [...data].sort()
    ipLoadError.value = ''
  } catch (e) {
    ipLoadError.value = errorMessage(e)
  } finally {
    ipLoading.value = false
  }
}

async function addIp() {
  const ip = newIp.value.trim()
  if (!ip || ipBusy.value) return
  if (!isValidIp(ip)) {
    ipError.value = t('server.invalidIp', { ip })
    return
  }
  ipError.value = ''
  ipBusy.value = true
  try {
    await ipBlocksApi.add(ip)
    newIp.value = ''
  } catch (e) {
    ipError.value = errorMessage(e, t('server.blockFailed', { ip }))
  } finally {
    ipBusy.value = false
    await loadIpBlocks()
  }
}

async function removeIp(ip: string) {
  if (!confirm(t('server.confirmUnblock', { ip }))) return
  ipError.value = ''
  ipBusy.value = true
  try {
    await ipBlocksApi.remove(ip)
  } catch (e) {
    ipError.value = errorMessage(e, t('server.unblockFailed', { ip }))
  } finally {
    ipBusy.value = false
    await loadIpBlocks()
  }
}

let schedulePoll: ReturnType<typeof setInterval> | null = null
let clock: ReturnType<typeof setInterval> | null = null

onMounted(() => {
  void loadSchedule()
  void loadIpBlocks()
  schedulePoll = setInterval(loadSchedule, 5000)
  clock = setInterval(() => (now.value = Date.now()), 1000)
})

onUnmounted(() => {
  if (schedulePoll) clearInterval(schedulePoll)
  if (clock) clearInterval(clock)
})

// --- Console ---
interface ConsoleLine { type: 'cmd' | 'resp'; text: string }

const consoleEl    = ref<HTMLElement | null>(null)
const cmdText      = ref('')
const consoleLines = ref<ConsoleLine[]>([])
const history      = ref<string[]>([])
const histIdx      = ref(-1)

async function runCommand() {
  const cmd = cmdText.value.trim()
  if (!cmd) return

  history.value.unshift(cmd)
  histIdx.value = -1
  consoleLines.value.push({ type: 'cmd', text: cmd })
  cmdText.value = ''

  try {
    const lines = await executeCommand(cmd)
    lines.forEach(l => consoleLines.value.push({ type: 'resp', text: l }))
  } catch {
    consoleLines.value.push({ type: 'resp', text: t('server.consoleError') })
  }

  await nextTick()
  const el = consoleEl.value
  if (el) el.scrollTop = el.scrollHeight
}

function historyUp() {
  if (history.value.length === 0) return
  histIdx.value = Math.min(histIdx.value + 1, history.value.length - 1)
  cmdText.value = history.value[histIdx.value] ?? ''
}

function historyDown() {
  histIdx.value = Math.max(histIdx.value - 1, -1)
  cmdText.value = histIdx.value >= 0 ? (history.value[histIdx.value] ?? '') : ''
}
</script>

<style scoped>
.server-page { display: flex; flex-direction: column; gap: 28px; }

.section { }

.section-title {
  font-size: 12px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted); margin: 0 0 14px;
}

.actions-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(200px, 1fr));
  gap: 12px;
}

.action-card {
  display: flex; flex-direction: column; align-items: flex-start;
  gap: 6px; padding: 18px;
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 10px; cursor: pointer; text-align: left;
  transition: border-color 0.15s, background 0.15s;
}

.action-card:hover:not(:disabled) { border-color: var(--accent); background: var(--bg-tertiary); }
.action-card:disabled { opacity: 0.5; cursor: not-allowed; }
.action-card.danger:hover:not(:disabled) { border-color: var(--danger); }

.action-icon { flex-shrink: 0; }
.action-icon.success { color: var(--success); }
.action-icon.accent  { color: var(--accent); }
.action-icon.warning { color: var(--warning); }
.action-icon.muted   { color: var(--text-muted); }
.action-icon.danger  { color: var(--danger); }

.action-label { font-size: 14px; font-weight: 600; color: var(--text-primary); }
.action-sub   { font-size: 12px; color: var(--text-muted); }

.broadcast-row { display: flex; gap: 10px; align-items: center; }

.broadcast-input {
  flex: 1; background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 14px;
  padding: 10px 14px; outline: none; transition: border-color 0.15s;
}

.broadcast-input:focus { border-color: var(--accent); }

.btn-accent {
  display: flex; align-items: center; gap: 6px;
  background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 10px 16px;
  cursor: pointer; white-space: nowrap; transition: background 0.15s;
}

.btn-accent:hover:not(:disabled) { background: var(--accent-hover); }
.btn-accent:disabled { opacity: 0.5; cursor: not-allowed; }

.sent-msg { font-size: 12px; color: var(--success); margin: 8px 0 0; }

.console-box {
  background: #0d1117; border: 1px solid var(--border);
  border-radius: 8px; overflow: hidden;
  display: flex; flex-direction: column;
}

.console-output {
  height: 280px; overflow-y: auto; padding: 14px;
  font-family: 'Courier New', Consolas, monospace; font-size: 12.5px; line-height: 1.7;
}

.console-empty { color: var(--text-muted); font-style: italic; }

.console-line { display: flex; gap: 4px; }
.console-prompt { color: var(--accent); }
.console-cmd  { color: var(--text-primary); font-weight: 600; }
.console-resp { color: var(--text-muted); }

.console-input-row {
  display: flex; align-items: center; gap: 8px;
  padding: 10px 14px; border-top: 1px solid var(--border);
  background: var(--bg-tertiary);
}

.prompt-label {
  color: var(--accent); font-family: 'Courier New', monospace;
  font-size: 14px; font-weight: 700;
}

.console-input {
  flex: 1; background: transparent; border: none;
  color: var(--text-primary); font-family: 'Courier New', Consolas, monospace;
  font-size: 13px; outline: none;
}

.spin { animation: spin 1s linear infinite; }

@keyframes spin { to { transform: rotate(360deg); } }

.feedback { font-size: 13px; color: var(--success); margin: 0; }

.error-msg { font-size: 12px; color: var(--danger); margin: 8px 0 0; }

.mono { font-family: 'Courier New', Consolas, monospace; }

.panel-box {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 10px; padding: 16px;
  display: flex; flex-direction: column; gap: 12px;
}

.panel-box .error-msg { margin: 0; }

.sched-row { display: flex; flex-wrap: wrap; gap: 12px; }

.seg {
  display: inline-flex; border: 1px solid var(--border);
  border-radius: 6px; overflow: hidden;
}

.seg button {
  display: flex; align-items: center; gap: 5px;
  padding: 7px 12px; border: none; border-right: 1px solid var(--border);
  background: transparent; color: var(--text-muted);
  font-size: 13px; font-weight: 500; cursor: pointer; transition: all 0.15s;
}

.seg button:last-child { border-right: none; }
.seg button:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.seg button.active { background: rgba(88,166,255,0.15); color: var(--accent); }

.pending-row {
  display: flex; align-items: center; justify-content: space-between; gap: 16px;
  flex-wrap: wrap;
}

.pending-info { display: flex; align-items: center; gap: 12px; }
.pending-title { font-size: 15px; font-weight: 600; color: var(--text-primary); }
.pending-sub { font-size: 12px; color: var(--text-muted); margin-top: 2px; }
.countdown { font-variant-numeric: tabular-nums; color: var(--warning); }

.btn-danger {
  display: flex; align-items: center; gap: 6px;
  background: transparent; color: var(--danger);
  border: 1px solid var(--danger); border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 8px 14px;
  cursor: pointer; transition: background 0.15s;
}

.btn-danger:hover:not(:disabled) { background: rgba(248,81,73,0.1); }
.btn-danger:disabled { opacity: 0.5; cursor: not-allowed; }

.note {
  display: flex; align-items: center; gap: 6px;
  font-size: 12px; color: var(--warning); margin: 0;
}

.ip-empty { font-size: 13px; color: var(--text-muted); }

.ip-list {
  list-style: none; margin: 0; padding: 0;
  display: flex; flex-direction: column;
  border: 1px solid var(--border); border-radius: 6px;
}

.ip-list li {
  display: flex; align-items: center; justify-content: space-between;
  padding: 6px 10px; font-size: 13px; color: var(--text-primary);
  border-bottom: 1px solid var(--border);
}

.ip-list li:last-child { border-bottom: none; }

.icon-btn {
  display: flex; align-items: center; justify-content: center;
  width: 26px; height: 26px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); cursor: pointer; transition: all 0.15s;
}

.icon-btn.danger:hover:not(:disabled) { border-color: var(--danger); color: var(--danger); }
.icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }
</style>
