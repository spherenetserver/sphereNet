<template>
  <div class="modal-overlay" @click.self="emit('close')">
    <div class="modal paperdoll-modal" role="dialog" :aria-label="t('paperdoll.title')">
      <div class="head">
        <h3>{{ t('paperdoll.title') }} <span class="mono text-muted">{{ hexSerial(serial) }}</span></h3>
        <button class="icon-btn plain" :title="t('paperdoll.close')" @click="emit('close')"><X :size="16" /></button>
      </div>

      <div v-if="loading" class="empty">
        <Loader2 :size="28" class="spin empty-icon" />
        <p>{{ t('paperdoll.loading') }}</p>
      </div>
      <div v-else-if="error" class="empty">
        <AlertTriangle :size="28" class="empty-icon error-icon" />
        <p>{{ error }}</p>
      </div>

      <div v-else-if="info" class="body">
        <div class="doll">
          <img v-if="imageUrl" :src="imageUrl" :alt="t('paperdoll.alt', { name: info.name })" />
          <p v-else-if="imageError" class="note error-msg">{{ imageError }}</p>
          <p v-else-if="!info.hasPaperdoll" class="note">{{ t('paperdoll.noPicture') }}</p>
          <Loader2 v-else :size="24" class="spin empty-icon" />
          <label v-if="info.hasPaperdoll" class="frame-toggle">
            <input v-model="frame" type="checkbox" /> {{ t('paperdoll.frame') }}
          </label>
        </div>

        <div class="details">
          <p class="doll-text">{{ info.paperdollText }}</p>
          <dl v-if="info.notoTitle || info.fameTitle || info.guildAbbrev" class="parts">
            <template v-if="info.notoTitle">
              <dt>{{ t('paperdoll.rank') }}</dt><dd>{{ info.notoTitle }}</dd>
            </template>
            <template v-if="info.fameTitle">
              <dt>{{ t('paperdoll.fameTitle') }}</dt><dd>{{ info.fameTitle }}</dd>
            </template>
            <template v-if="info.guildAbbrev">
              <dt>{{ t('paperdoll.guild') }}</dt>
              <dd>[{{ info.guildAbbrev }}]<span v-if="info.guildTitle"> {{ info.guildTitle }}</span></dd>
            </template>
          </dl>
          <div class="badges">
            <span v-if="info.isPlayer" class="badge" :class="info.online ? 'on' : 'off'">
              {{ info.online ? t('paperdoll.online') : t('paperdoll.offline') }}
            </span>
            <span v-else class="badge off">{{ t('paperdoll.npc') }}</span>
            <span v-if="info.accountName" class="text-muted small">{{ t('paperdoll.account', { name: info.accountName }) }}</span>
          </div>

          <h4>{{ t('paperdoll.equipment') }}</h4>
          <table v-if="info.equipment.length > 0" class="table">
            <thead>
              <tr>
                <th>{{ t('paperdoll.colLayer') }}</th>
                <th>{{ t('paperdoll.colItem') }}</th>
                <th>{{ t('paperdoll.colId') }}</th>
                <th>{{ t('paperdoll.colHue') }}</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="e in info.equipment" :key="e.layer">
                <td class="text-muted">{{ layerName(e.layer) }}</td>
                <td>{{ e.name }}</td>
                <td class="mono">{{ hexId(e.dispId) }}</td>
                <td class="mono">{{ e.hue ? hexId(e.hue) : '—' }}</td>
              </tr>
            </tbody>
          </table>
          <p v-else class="text-muted small">{{ t('paperdoll.noEquipment') }}</p>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { AlertTriangle, Loader2, X } from 'lucide-vue-next'
import { errorMessage, paperdollApi, type PaperdollInfo } from '@/lib/api'
import { hexId, hexSerial, layerName, readFramePreference, saveFramePreference } from '@/lib/paperdoll'
import { t } from '@/i18n'

const props = defineProps<{ serial: number }>()
const emit = defineEmits<{ close: [] }>()

const info       = ref<PaperdollInfo | null>(null)
const loading    = ref(true)
const error      = ref('')
const imageUrl   = ref('')
const imageError = ref('')
// The framed picture carries the name line the way the game client shows it,
// so it is the default; the viewer's own choice is remembered.
const frame      = ref(readFramePreference())

function revokeImage() {
  if (imageUrl.value) URL.revokeObjectURL(imageUrl.value)
  imageUrl.value = ''
}

// Only the newest picture request may land (the frame toggle can be clicked
// again before the previous picture arrives).
let imageRequest = 0

async function loadImage() {
  const request = ++imageRequest
  revokeImage()
  imageError.value = ''
  if (!info.value?.hasPaperdoll) return
  try {
    const { data } = await paperdollApi.png(props.serial, frame.value)
    if (request !== imageRequest) return
    imageUrl.value = URL.createObjectURL(data)
  } catch (e) {
    if (request !== imageRequest) return
    imageError.value = t('paperdoll.pictureError', { error: errorMessage(e) })
  }
}

async function load() {
  loading.value = true
  error.value = ''
  info.value = null
  try {
    const { data } = await paperdollApi.info(props.serial)
    info.value = data
  } catch (e) {
    const status = (e as { response?: { status?: number } })?.response?.status
    error.value = status === 404
      ? t('paperdoll.notFound', { serial: hexSerial(props.serial) })
      : t('paperdoll.loadError', { error: errorMessage(e) })
  } finally {
    loading.value = false
  }
  await loadImage()
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'Escape') emit('close')
}

onMounted(() => {
  window.addEventListener('keydown', onKey)
  load()
})
onBeforeUnmount(() => {
  window.removeEventListener('keydown', onKey)
  imageRequest++
  revokeImage()
})
watch(() => props.serial, load)
watch(frame, value => {
  saveFramePreference(value)
  loadImage()
})
</script>

<style scoped>
.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.6);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}

.modal {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 12px; padding: 24px; width: 760px; max-width: calc(100vw - 32px);
  max-height: calc(100vh - 32px); overflow-y: auto;
  display: flex; flex-direction: column; gap: 16px;
}

.head { display: flex; align-items: center; justify-content: space-between; gap: 12px; }
.head h3 { margin: 0; font-size: 16px; font-weight: 600; display: flex; gap: 10px; align-items: baseline; }

.body { display: flex; gap: 24px; flex-wrap: wrap; }

.doll {
  display: flex; flex-direction: column; align-items: center; gap: 10px;
  min-width: 280px;
}

.doll img { image-rendering: pixelated; max-width: 100%; }

.frame-toggle { font-size: 12px; color: var(--text-muted); display: flex; gap: 6px; align-items: center; cursor: pointer; }

.details { flex: 1; min-width: 260px; display: flex; flex-direction: column; gap: 10px; }

.doll-text { margin: 0; font-size: 15px; font-weight: 600; color: var(--text-primary); }

.details h4 {
  margin: 6px 0 0; font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted);
}

.parts {
  margin: 0; display: grid; grid-template-columns: max-content 1fr; gap: 4px 12px; font-size: 13px;
}
.parts dt { color: var(--text-muted); }
.parts dd { margin: 0; color: var(--text-primary); }

.badges { display: flex; gap: 10px; align-items: center; flex-wrap: wrap; }

.badge { display: inline-block; padding: 2px 8px; border-radius: 9999px; font-size: 11px; font-weight: 600; }
.badge.on  { background: rgba(63,185,80,0.12); color: var(--success); }
.badge.off { background: rgba(139,148,158,0.15); color: var(--text-muted); }

.table { width: 100%; border-collapse: collapse; font-size: 13px; }
.table th {
  text-align: left; padding: 6px 8px; font-size: 11px; font-weight: 600; color: var(--text-muted);
  border-bottom: 1px solid var(--border);
}
.table td { padding: 6px 8px; border-bottom: 1px solid var(--border); color: var(--text-primary); }
.table tr:last-child td { border-bottom: none; }

.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }
.small { font-size: 12px; margin: 0; }
.note { font-size: 13px; color: var(--text-muted); margin: 0; }
.error-msg { color: var(--danger); }

.empty { display: flex; flex-direction: column; align-items: center; gap: 10px; padding: 40px; color: var(--text-muted); }
.empty p { margin: 0; font-size: 14px; }
.empty-icon { opacity: 0.4; }
.error-icon { color: var(--danger); opacity: 0.8; }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

.icon-btn {
  display: flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 6px;
  border: 1px solid transparent; background: transparent;
  color: var(--text-muted); cursor: pointer;
}
.icon-btn:hover { background: var(--bg-tertiary); color: var(--text-primary); }
</style>
