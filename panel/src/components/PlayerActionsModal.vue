<template>
  <div class="modal-overlay" @click.self="emit('close')">
    <div class="modal actions-modal" role="dialog" :aria-label="t('actions.title')">
      <div class="head">
        <h3>
          {{ detail?.name ?? nameHint ?? t('actions.title') }}
          <span class="mono text-muted">{{ hexSerial(serial) }}</span>
        </h3>
        <div class="head-buttons">
          <button class="icon-btn" :title="t('players.openPaperdoll')" @click="emit('paperdoll', serial)">
            <UserSquare :size="15" />
          </button>
          <button class="icon-btn" :title="t('common.refresh')" :disabled="loading" @click="loadDetail">
            <RefreshCw :size="15" :class="{ spin: loading }" />
          </button>
          <button class="icon-btn plain" :title="t('paperdoll.close')" @click="emit('close')"><X :size="16" /></button>
        </div>
      </div>

      <div v-if="loading && !detail" class="empty">
        <Loader2 :size="28" class="spin empty-icon" />
        <p>{{ t('common.loading') }}</p>
      </div>
      <div v-else-if="loadError && !detail" class="empty">
        <AlertTriangle :size="28" class="empty-icon error-icon" />
        <p>{{ loadError }}</p>
      </div>

      <template v-if="detail">
        <!-- State badges -->
        <div class="badges">
          <span class="badge" :class="detail.online ? 'on' : 'off'">
            {{ detail.online ? t('paperdoll.online') : t('paperdoll.offline') }}
          </span>
          <span v-if="!detail.isPlayer" class="badge off">{{ t('paperdoll.npc') }}</span>
          <span v-if="detail.dead" class="badge bad">{{ t('actions.stateDead') }}</span>
          <span v-if="detail.frozen" class="badge warn">{{ t('actions.stateFrozen') }}</span>
          <span v-if="detail.hidden" class="badge warn">{{ t('actions.stateHidden') }}</span>
          <span v-if="detail.poisoned" class="badge bad">{{ t('actions.statePoisoned') }}</span>
          <span v-if="detail.jailed" class="badge bad">{{ t('actions.stateJailed') }}</span>
          <span class="badge" :class="`noto-${detail.notoriety}`">{{ detail.notorietyName }}</span>
          <span v-if="detail.accountName" class="text-muted small">
            {{ t('paperdoll.account', { name: detail.accountName }) }} · {{ privLabel(detail.privLevel) }}
          </span>
        </div>

        <!-- Stats -->
        <div class="stats-grid">
          <div class="stat"><span class="k">STR</span><span class="v">{{ detail.str }}</span></div>
          <div class="stat"><span class="k">DEX</span><span class="v">{{ detail.dex }}</span></div>
          <div class="stat"><span class="k">INT</span><span class="v">{{ detail.int }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.hits') }}</span><span class="v">{{ detail.hits }} / {{ detail.maxHits }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.mana') }}</span><span class="v">{{ detail.mana }} / {{ detail.maxMana }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.stam') }}</span><span class="v">{{ detail.stam }} / {{ detail.maxStam }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.fame') }}</span><span class="v">{{ detail.fame }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.karma') }}</span><span class="v">{{ detail.karma }}</span></div>
          <div class="stat"><span class="k">{{ t('actions.kills') }}</span><span class="v">{{ detail.kills }}</span></div>
          <div class="stat wide">
            <span class="k">{{ t('actions.position') }}</span>
            <span class="v mono">{{ detail.x }}, {{ detail.y }}, {{ detail.z }} · {{ mapName(detail.mapId) }}</span>
          </div>
        </div>

        <!-- Speak / message -->
        <section class="block">
          <h4>{{ t('actions.speakTitle') }}</h4>
          <div class="row">
            <div class="seg">
              <button v-for="m in speakModes" :key="m" :class="{ active: speakMode === m }" @click="speakMode = m">
                {{ t(modeLabels[m]) }}
              </button>
            </div>
            <input v-if="speakMode === 'message'" v-model="hueText" class="input hue" :placeholder="t('actions.huePlaceholder')" />
          </div>
          <div class="row">
            <input v-model="speakText" class="input grow" maxlength="256"
              :placeholder="t(modePlaceholders[speakMode])" @keyup.enter="speak" />
            <button class="btn-accent" :disabled="running || !speakText.trim() || (speakMode === 'message' && !detail.online)"
              @click="speak">
              <Send :size="14" /> {{ t('players.send') }}
            </button>
          </div>
          <p v-if="speakMode === 'message' && !detail.online" class="hint">{{ t('actions.messageNeedsOnline') }}</p>
          <p v-if="hueError" class="error-msg">{{ hueError }}</p>
        </section>

        <!-- Verb / function -->
        <section class="block">
          <h4>{{ t('actions.verbTitle') }}</h4>
          <div class="row">
            <div class="seg">
              <button :class="{ active: verbMode === 'command' }" :disabled="!detail.online" @click="verbMode = 'command'">
                {{ t('actions.verbAsSelf') }}
              </button>
              <button :class="{ active: verbMode === 'verb' }" @click="verbMode = 'verb'">
                {{ t('actions.verbAsPanel') }}
              </button>
            </div>
          </div>
          <div class="row">
            <input v-model="verbText" class="input grow mono" maxlength="512"
              :placeholder="t('actions.verbPlaceholder')" @keyup.enter="runVerb" />
            <button class="btn-accent" :disabled="running || !verbText.trim() || (verbMode === 'command' && !detail.online)" @click="runVerb">
              <Play :size="14" /> {{ t('actions.run') }}
            </button>
          </div>
          <p class="hint">{{ t(verbMode === 'command' ? 'actions.verbAsSelfHint' : 'actions.verbHint') }}</p>
        </section>

        <!-- Quick actions -->
        <section class="block">
          <h4>{{ t('actions.quickTitle') }}</h4>
          <div class="quick">
            <button class="btn-ghost" :disabled="running" @click="run({ action: 'heal' })">
              <HeartPulse :size="14" /> {{ t('actions.heal') }}
            </button>
            <button class="btn-ghost" :disabled="running || !detail.dead" @click="run({ action: 'resurrect' })">
              <Sparkles :size="14" /> {{ t('actions.resurrect') }}
            </button>
            <button class="btn-ghost" :disabled="running" @click="run({ action: detail.frozen ? 'unfreeze' : 'freeze' })">
              <Snowflake :size="14" /> {{ detail.frozen ? t('actions.unfreeze') : t('actions.freeze') }}
            </button>
            <button class="btn-ghost" :disabled="running" @click="run({ action: detail.hidden ? 'unhide' : 'hide' })">
              <component :is="detail.hidden ? Eye : EyeOff" :size="14" />
              {{ detail.hidden ? t('actions.unhide') : t('actions.hide') }}
            </button>
            <button v-if="detail.jailed" class="btn-ghost" :disabled="running" @click="run({ action: 'unjail' })">
              <DoorOpen :size="14" /> {{ t('actions.unjail') }}
            </button>
            <button v-else class="btn-ghost danger" :disabled="running" @click="askConfirm('jail')">
              <Lock :size="14" /> {{ t('actions.jail') }}
            </button>
            <button class="btn-ghost danger" :disabled="running || detail.dead" @click="askConfirm('kill')">
              <Skull :size="14" /> {{ t('actions.kill') }}
            </button>
          </div>

          <div v-if="confirming" class="confirm-box">
            <span>{{ confirming === 'kill'
              ? t('actions.confirmKill', { name: detail.name })
              : t('actions.confirmJail', { name: detail.name }) }}</span>
            <label v-if="confirming === 'jail'" class="inline-field">
              {{ t('actions.jailMinutes') }}
              <input v-model="jailMinutes" class="input small-input" inputmode="numeric" />
            </label>
            <div class="confirm-buttons">
              <button class="btn-ghost" @click="confirming = null">{{ t('common.cancel') }}</button>
              <button class="btn-danger" :disabled="running" @click="confirmAction">{{ t('actions.confirm') }}</button>
            </div>
          </div>
        </section>

        <!-- Teleport -->
        <section class="block">
          <h4>{{ t('actions.teleportTitle') }}</h4>
          <div class="row">
            <input v-model="tp.x" class="input coord" placeholder="X" inputmode="numeric" />
            <input v-model="tp.y" class="input coord" placeholder="Y" inputmode="numeric" />
            <input v-model="tp.z" class="input coord" :placeholder="t('actions.zOptional')" inputmode="numeric" />
            <select v-model="tp.map" class="input">
              <option value="">{{ t('actions.sameMap') }}</option>
              <option v-for="(n, id) in mapNames" :key="id" :value="String(id)">{{ n }}</option>
            </select>
            <button class="btn-accent" :disabled="running" @click="teleport">
              <MapPin :size="14" /> {{ t('actions.teleport') }}
            </button>
          </div>
          <p v-if="tpError" class="error-msg">{{ tpError }}</p>
        </section>

        <!-- Output -->
        <section class="block">
          <h4>{{ t('actions.outputTitle') }}</h4>
          <div class="output">
            <div v-if="output.length === 0" class="text-muted small">{{ t('actions.outputEmpty') }}</div>
            <div v-for="(o, i) in output" :key="i" :class="['out-line', o.kind]">{{ o.text }}</div>
          </div>
        </section>

        <!-- Skills -->
        <section class="block">
          <h4>{{ t('actions.skillsTitle', { n: detail.skills.length }) }}</h4>
          <table v-if="detail.skills.length > 0" class="table">
            <tbody>
              <tr v-for="s in sortedSkills" :key="s.id">
                <td>{{ s.name }}</td>
                <td class="mono num">{{ (s.value / 10).toFixed(1) }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="text-muted small">{{ t('actions.noSkills') }}</p>
        </section>

        <!-- Tags -->
        <section class="block">
          <h4>{{ t('actions.tagsTitle', { n: detail.tags.length }) }}</h4>
          <table v-if="detail.tags.length > 0" class="table">
            <tbody>
              <tr v-for="tag in detail.tags" :key="tag.key">
                <td class="mono">{{ tag.key }}</td>
                <td class="mono tag-value">{{ tag.value }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="text-muted small">{{ t('actions.noTags') }}</p>
          <p v-if="detail.tagsTruncated" class="hint">{{ t('actions.tagsTruncated') }}</p>
        </section>
      </template>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import {
  AlertTriangle, DoorOpen, Eye, EyeOff, HeartPulse, Loader2, Lock, MapPin, Play, RefreshCw,
  Send, Skull, Snowflake, Sparkles, UserSquare, X,
} from 'lucide-vue-next'
import {
  errorMessage, playersApi, type PlayerActionRequest, type PlayerDetail,
} from '@/lib/api'
import { hexSerial } from '@/lib/paperdoll'
import { t, type MessageKey } from '@/i18n'

const props = defineProps<{ serial: number; nameHint?: string }>()
const emit = defineEmits<{ close: []; paperdoll: [serial: number] }>()

const mapNames: Record<number, string> = {
  0: 'Felucca', 1: 'Trammel', 2: 'Ilshenar', 3: 'Malas', 4: 'Tokuno', 5: 'TerMur',
}
function mapName(id: number): string { return mapNames[id] ?? `Map${id}` }

const privLabels: Record<number, string> = {
  0: 'Guest', 1: 'Player', 2: 'Counselor', 3: 'Seer', 4: 'GM', 5: 'Dev', 6: 'Admin', 7: 'Owner',
}
function privLabel(n: number): string { return privLabels[n] ?? `L${n}` }

// --- Detail ---
const detail    = ref<PlayerDetail | null>(null)
const loading   = ref(false)
const loadError = ref('')

async function loadDetail() {
  loading.value = true
  try {
    const { data } = await playersApi.detail(props.serial)
    detail.value = data
    loadError.value = ''
  } catch (e) {
    const status = (e as { response?: { status?: number } }).response?.status
    loadError.value = status === 404
      ? t('paperdoll.notFound', { serial: hexSerial(props.serial) })
      : errorMessage(e)
  } finally {
    loading.value = false
  }
}

onMounted(loadDetail)

const sortedSkills = computed(() =>
  [...(detail.value?.skills ?? [])].sort((a, b) => b.value - a.value || a.name.localeCompare(b.name)))

// --- Running actions ---
interface OutLine { kind: 'ok' | 'fail' | 'cmd'; text: string }
const output  = ref<OutLine[]>([])
const running = ref(false)

function log(kind: OutLine['kind'], text: string) {
  output.value.unshift({ kind, text })
  if (output.value.length > 200) output.value.length = 200
}

async function run(req: PlayerActionRequest, label?: string): Promise<boolean> {
  running.value = true
  log('cmd', `» ${label ?? req.action}`)
  try {
    const { data } = await playersApi.action(props.serial, req)
    // Newest first: the lines are pushed in reverse so they read top-down.
    for (const line of [...data.lines].reverse()) log(data.ok ? 'ok' : 'fail', line)
    return data.ok
  } catch (e) {
    log('fail', errorMessage(e, t('actions.failed')))
    return false
  } finally {
    running.value = false
    void loadDetail()
  }
}

// --- Speak ---
const speakModes = ['say', 'emote', 'message'] as const
type SpeakMode = typeof speakModes[number]
const modeLabels: Record<SpeakMode, MessageKey> = {
  say: 'actions.modeSay', emote: 'actions.modeEmote', message: 'actions.modeMessage',
}
const modePlaceholders: Record<SpeakMode, MessageKey> = {
  say: 'actions.placeholderSay', emote: 'actions.placeholderEmote', message: 'actions.placeholderMessage',
}
const speakMode = ref<SpeakMode>('say')
const speakText = ref('')
const hueText   = ref('')
const hueError  = ref('')

function parseHue(raw: string): number | null | undefined {
  const s = raw.trim()
  if (!s) return undefined
  const v = /^0x[0-9a-f]+$/i.test(s) ? parseInt(s.slice(2), 16) : /^\d+$/.test(s) ? parseInt(s, 10) : NaN
  return Number.isInteger(v) && v >= 0 && v <= 0xFFFF ? v : null
}

async function speak() {
  const text = speakText.value.trim()
  if (!text || running.value) return
  hueError.value = ''
  const req: PlayerActionRequest = { action: speakMode.value, text }
  if (speakMode.value === 'message') {
    const hue = parseHue(hueText.value)
    if (hue === null) { hueError.value = t('actions.invalidHue'); return }
    if (hue !== undefined) req.hue = hue
  }
  if (await run(req, `${speakMode.value} ${text}`)) speakText.value = ''
}

// --- Verb ---
const verbText = ref('')
// 'command' runs the line as the player's own .command (SRC = the character,
// its plevel, output on its client); 'verb' runs it on the character with the
// panel as an Owner-level source and returns the output here.
const verbMode = ref<'command' | 'verb'>('verb')
watch(() => detail.value?.online, online => { verbMode.value = online ? 'command' : 'verb' }, { immediate: true })

async function runVerb() {
  const text = verbText.value.trim()
  if (!text || running.value) return
  const action = verbMode.value === 'command' && detail.value?.online ? 'command' : 'verb'
  await run({ action, text }, action === 'command' ? `.${text.replace(/^[./]+/, '')}` : text)
}

// --- Dangerous actions: confirmed in place ---
const confirming  = ref<'kill' | 'jail' | null>(null)
const jailMinutes = ref('')

function askConfirm(what: 'kill' | 'jail') {
  confirming.value = what
  jailMinutes.value = ''
}

async function confirmAction() {
  const what = confirming.value
  if (!what) return
  if (what === 'kill') {
    confirming.value = null
    await run({ action: 'kill' })
    return
  }
  const raw = jailMinutes.value.trim()
  const minutes = raw === '' ? 0 : Number(raw)
  if (!Number.isInteger(minutes) || minutes < 0) {
    log('fail', t('actions.invalidMinutes'))
    return
  }
  confirming.value = null
  await run({ action: 'jail', minutes }, minutes > 0 ? `jail ${minutes}` : 'jail')
}

// --- Teleport ---
const tp      = ref({ x: '', y: '', z: '', map: '' })
const tpError = ref('')

function toInt(raw: string): number | null {
  const s = raw.trim()
  return /^-?\d+$/.test(s) ? parseInt(s, 10) : null
}

async function teleport() {
  tpError.value = ''
  const x = toInt(tp.value.x)
  const y = toInt(tp.value.y)
  if (x === null || y === null || x < 0 || y < 0) {
    tpError.value = t('actions.invalidCoords')
    return
  }
  const req: PlayerActionRequest = { action: 'teleport', x, y }
  if (tp.value.z.trim()) {
    const z = toInt(tp.value.z)
    if (z === null || z < -128 || z > 127) { tpError.value = t('actions.invalidZ'); return }
    req.z = z
  }
  if (tp.value.map !== '') req.map = Number(tp.value.map)
  await run(req, `teleport ${x},${y}${req.z !== undefined ? ',' + req.z : ''}${req.map !== undefined ? ' map ' + req.map : ''}`)
}
</script>

<style scoped>
.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.6);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}

.modal {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 12px; padding: 24px; width: 820px; max-width: calc(100vw - 32px);
  max-height: calc(100vh - 32px); overflow-y: auto;
  display: flex; flex-direction: column; gap: 16px;
}

.head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.head h3 { margin: 0; font-size: 16px; font-weight: 600; display: flex; gap: 10px; align-items: baseline; }
.head-buttons { display: flex; gap: 6px; align-items: center; }

.badges { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.badge {
  display: inline-block; padding: 2px 8px; border-radius: 9999px; font-size: 11px; font-weight: 600;
  background: rgba(139,148,158,0.15); color: var(--text-muted);
}
.badge.on   { background: rgba(63,185,80,0.12); color: var(--success); }
.badge.warn { background: rgba(210,153,34,0.15); color: var(--warning); }
.badge.bad  { background: rgba(248,81,73,0.12); color: var(--danger); }
.noto-1 { background: rgba(88,166,255,0.12); color: var(--accent); }
.noto-2 { background: rgba(63,185,80,0.12); color: var(--success); }
.noto-4, .noto-5 { background: rgba(210,153,34,0.15); color: var(--warning); }
.noto-6 { background: rgba(248,81,73,0.12); color: var(--danger); }
.noto-7 { background: rgba(210,153,34,0.2); color: var(--warning); }

.stats-grid {
  display: grid; grid-template-columns: repeat(auto-fill, minmax(120px, 1fr)); gap: 8px;
}
.stat {
  display: flex; flex-direction: column; gap: 2px; padding: 8px 10px;
  background: var(--bg-tertiary); border: 1px solid var(--border); border-radius: 6px;
}
.stat.wide { grid-column: span 2; }
.stat .k { font-size: 10px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em; color: var(--text-muted); }
.stat .v { font-size: 14px; font-weight: 600; color: var(--text-primary); }

.block { display: flex; flex-direction: column; gap: 8px; }
.block h4 {
  margin: 0; font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted);
}

.row { display: flex; gap: 8px; align-items: center; flex-wrap: wrap; }
.grow { flex: 1; min-width: 200px; }

.input {
  background: var(--bg-tertiary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 13px; padding: 7px 10px; outline: none;
}
.input:focus { border-color: var(--accent); }
.input.hue { width: 150px; }
.input.coord { width: 90px; }
.small-input { width: 80px; margin-left: 6px; }

.seg { display: inline-flex; border: 1px solid var(--border); border-radius: 6px; overflow: hidden; }
.seg button {
  padding: 6px 12px; border: none; border-right: 1px solid var(--border);
  background: transparent; color: var(--text-muted); font-size: 13px; font-weight: 500; cursor: pointer;
}
.seg button:last-child { border-right: none; }
.seg button:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.seg button.active { background: rgba(88,166,255,0.15); color: var(--accent); }

.quick { display: flex; flex-wrap: wrap; gap: 8px; }

.btn-ghost {
  display: flex; align-items: center; gap: 6px; padding: 6px 12px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent; color: var(--text-muted);
  font-size: 13px; font-weight: 500; cursor: pointer; transition: all 0.15s;
}
.btn-ghost:hover:not(:disabled) { background: var(--bg-tertiary); color: var(--text-primary); }
.btn-ghost.danger:hover:not(:disabled) { border-color: var(--danger); color: var(--danger); }
.btn-ghost:disabled { opacity: 0.4; cursor: not-allowed; }

.btn-accent {
  display: flex; align-items: center; gap: 6px; background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px; font-size: 13px; font-weight: 600; padding: 7px 14px; cursor: pointer;
}
.btn-accent:hover:not(:disabled) { background: var(--accent-hover); }
.btn-accent:disabled { opacity: 0.5; cursor: not-allowed; }

.btn-danger {
  display: flex; align-items: center; gap: 6px; background: transparent; color: var(--danger);
  border: 1px solid var(--danger); border-radius: 6px; font-size: 13px; font-weight: 600;
  padding: 6px 12px; cursor: pointer;
}
.btn-danger:hover:not(:disabled) { background: rgba(248,81,73,0.1); }
.btn-danger:disabled { opacity: 0.5; cursor: not-allowed; }

.confirm-box {
  display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 10px;
  padding: 10px 12px; border: 1px solid var(--danger); border-radius: 6px;
  background: rgba(248,81,73,0.08); color: var(--text-primary); font-size: 13px;
}
.confirm-buttons { display: flex; gap: 8px; }
.inline-field { font-size: 12px; color: var(--text-muted); display: flex; align-items: center; }

.output {
  background: #0d1117; border: 1px solid var(--border); border-radius: 6px;
  padding: 10px 12px; max-height: 180px; overflow-y: auto;
  font-family: 'Courier New', Consolas, monospace; font-size: 12.5px; line-height: 1.6;
}
.out-line.cmd  { color: var(--accent); }
.out-line.ok   { color: var(--text-primary); }
.out-line.fail { color: var(--danger); }

.table { width: 100%; border-collapse: collapse; font-size: 13px; }
.table td { padding: 5px 8px; border-bottom: 1px solid var(--border); color: var(--text-primary); }
.table tr:last-child td { border-bottom: none; }
.num { text-align: right; }
.tag-value { word-break: break-all; color: var(--text-muted); }

.hint { font-size: 12px; color: var(--text-muted); margin: 0; }
.error-msg { font-size: 12px; color: var(--danger); margin: 0; }
.small { font-size: 12px; }
.mono { font-family: 'Courier New', monospace; }
.text-muted { color: var(--text-muted); }

.icon-btn {
  display: flex; align-items: center; justify-content: center; width: 28px; height: 28px;
  border-radius: 6px; border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); cursor: pointer; transition: all 0.15s;
}
.icon-btn:hover:not(:disabled) { background: var(--bg-tertiary); color: var(--text-primary); }
.icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }
.icon-btn.plain { border-color: transparent; }

.empty { display: flex; flex-direction: column; align-items: center; gap: 12px; padding: 40px; color: var(--text-muted); }
.empty-icon { opacity: 0.3; }
.error-icon { color: var(--danger); opacity: 0.8; }
.empty p { margin: 0; font-size: 14px; }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
