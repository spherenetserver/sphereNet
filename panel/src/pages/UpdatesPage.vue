<template>
  <div class="updates-page">

    <!-- Updater not configured — sphere.ini has no AppUpdateRepo -->
    <section v-if="!store.supported" class="section">
      <div class="notice">
        <PackageX :size="20" class="notice-icon muted" />
        <div>
          <p class="notice-title">{{ t('updates.notConfiguredTitle') }}</p>
          <p class="notice-sub">
            <I18nT k="updates.notConfiguredText">
              <template #file><code>config/sphere.ini</code></template>
              <template #key><code>APPUPDATEREPO</code></template>
            </I18nT>
            {{ t('updates.example') }} <code>APPUPDATEREPO=spherenetserver/sphereNet</code>
          </p>
        </div>
      </div>
    </section>

    <template v-else>
      <!-- Status -->
      <section class="section">
        <div class="status-head">
          <h2 class="section-title">{{ t('updates.statusTitle') }}</h2>
          <button class="btn-ghost" :disabled="store.checking || store.busy" @click="store.check()">
            <RefreshCw :size="15" :class="{ spin: store.checking }" />
            {{ store.checking ? t('updates.checking') : t('updates.check') }}
          </button>
        </div>

        <div class="status-card" :class="statusTone">
          <component :is="statusIcon" :size="26" class="status-icon" :class="statusTone" />
          <div class="status-body">
            <p class="status-title">{{ statusTitle }}</p>
            <p v-if="store.status?.message" class="status-msg">{{ store.status.message }}</p>
            <p v-if="store.error" class="status-msg danger">{{ store.error }}</p>
          </div>

          <button
            v-if="store.available && store.status?.canApply"
            class="btn-accent"
            :disabled="store.busy"
            @click="confirmApply"
          >
            <Download :size="15" /> {{ t('updates.applyNow') }}
          </button>
        </div>

        <!-- Download / apply progress -->
        <div v-if="store.busy" class="progress-wrap">
          <div class="progress-track">
            <div
              class="progress-fill"
              :class="{ indeterminate: progressPercent === 0 }"
              :style="progressPercent > 0 ? { width: progressPercent + '%' } : undefined"
            />
          </div>
          <span class="progress-label">
            {{ stateLabel }}<template v-if="progressPercent > 0"> — {{ progressPercent }}%</template>
          </span>
        </div>

        <p v-if="store.restarting" class="restart-note">
          <Loader :size="14" class="spin" />
          {{ t('updates.restarting') }}
        </p>
      </section>

      <!-- What the running binary says about ITSELF -->
      <section class="section">
        <h2 class="section-title">{{ t('updates.runningTitle') }}</h2>
        <p class="running-hint">
          {{ t('updates.runningHint') }}
        </p>
        <dl v-if="running" class="meta">
          <div><dt>{{ t('updates.commit') }}</dt><dd><code>{{ running.stamped ? running.shortCommit : '—' }}</code></dd></div>
          <div><dt>{{ t('updates.branch') }}</dt><dd><code>{{ running.branch || '—' }}</code></dd></div>
          <div>
            <dt>{{ t('updates.localChanges') }}</dt>
            <dd>{{ running.dirty === null ? t('updates.dirtyUnknown') : (running.dirty ? t('updates.dirtyYes') : t('updates.dirtyNo')) }}</dd>
          </div>
          <div><dt>{{ t('updates.assembly') }}</dt><dd><code>{{ running.assemblyVersion }}</code></dd></div>
        </dl>
        <p v-else-if="runningError" class="vc-empty">{{ runningError }}</p>
        <p v-else class="vc-empty">{{ t('updates.reading') }}</p>

        <p v-if="running && !running.stamped" class="running-warn">
          {{ t('updates.unstamped') }}
        </p>
        <p v-else-if="running?.dirty === true" class="running-warn">
          {{ t('updates.dirtyBuild') }}
        </p>
      </section>

      <!-- Versions -->
      <section class="section">
        <h2 class="section-title">{{ t('updates.versions') }}</h2>
        <div class="version-grid">
          <div v-for="card in versionCards" :key="card.title" class="version-card">
            <p class="vc-title">{{ card.title }}</p>
            <dl v-if="card.version" class="vc-list">
              <div class="vc-row"><dt>{{ t('updates.commit') }}</dt><dd><code>{{ card.version.shortSha }}</code></dd></div>
              <div class="vc-row"><dt>{{ t('updates.build') }}</dt><dd>#{{ card.version.buildNumber }}</dd></div>
              <div class="vc-row"><dt>{{ t('updates.branch') }}</dt><dd><code>{{ card.version.branch }}</code></dd></div>
              <div class="vc-row"><dt>{{ t('updates.date') }}</dt><dd>{{ fmtDateTime(card.version.builtAt) }}</dd></div>
              <div class="vc-row subject" :title="card.version.commitSubject">
                <dt>{{ t('updates.subject') }}</dt><dd>{{ card.version.commitSubject || '—' }}</dd>
              </div>
            </dl>
            <p v-else class="vc-empty">{{ card.emptyText }}</p>
          </div>
        </div>

        <dl class="meta">
          <div><dt>{{ t('updates.repo') }}</dt><dd><code>{{ store.status?.repo ?? '—' }}</code></dd></div>
          <div><dt>{{ t('updates.channel') }}</dt><dd><code>{{ store.status?.channel ?? '—' }}</code></dd></div>
          <div><dt>{{ t('updates.platform') }}</dt><dd><code>{{ store.status?.runtime ?? '—' }}</code></dd></div>
          <div><dt>{{ t('updates.lastCheck') }}</dt><dd>{{ lastChecked }}</dd></div>
        </dl>
      </section>

      <!-- Why apply may be unavailable -->
      <section v-if="store.status && !store.status.canApply" class="section">
        <div class="notice">
          <Info :size="20" class="notice-icon warning" />
          <div>
            <p class="notice-title">{{ t('updates.cannotApplyTitle') }}</p>
            <p class="notice-sub">{{ cannotApplyReason }}</p>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import {
  RefreshCw, Download, CheckCircle2, AlertTriangle, PackageX, Info, Loader, Sparkles,
} from 'lucide-vue-next'
import { useUpdateStore } from '@/stores/update'
import { serverApi, type RunningBuild, type UpdateStateName } from '@/lib/api'
import { t, fmtDateTime, type MessageKey } from '@/i18n'
import I18nT from '@/i18n/I18nT'

const store = useUpdateStore()

// Read straight from the server rather than through the update store: the store
// describes what the updater knows about, and the whole point here is to show
// what is actually running, including when the two disagree.
const running = ref<RunningBuild | null>(null)
const runningError = ref('')
onMounted(async () => {
  try {
    const { data } = await serverApi.version()
    running.value = data
  } catch (e) {
    runningError.value = e instanceof Error ? e.message : t('updates.versionReadFailed')
  }
})

// Refresh on entry so the page never opens on a minute-old snapshot. The
// Sidebar owns the long-lived poll, so this must not stop it on unmount —
// start() reuses the existing timer rather than stacking a second one.
onMounted(() => void store.start())

const progressPercent = computed(() => store.status?.progressPercent ?? 0)

const stateKeys: Record<UpdateStateName, MessageKey> = {
  Idle:        'updates.stateIdle',
  Checking:    'updates.stateChecking',
  Downloading: 'updates.stateDownloading',
  Verifying:   'updates.stateVerifying',
  Extracting:  'updates.stateExtracting',
  Staged:      'updates.stateStaged',
  Applying:    'updates.stateApplying',
  Failed:      'updates.stateFailed',
}
const stateLabel = computed(() => t(stateKeys[store.status?.state ?? 'Idle']))

const statusTone = computed(() => {
  if (store.status?.state === 'Failed') return 'danger'
  if (store.available) return 'accent'
  if (store.status?.isDevBuild) return 'muted'
  return 'success'
})

const statusIcon = computed(() => {
  if (store.status?.state === 'Failed') return AlertTriangle
  if (store.available) return Sparkles
  if (store.status?.isDevBuild) return Info
  return CheckCircle2
})

const statusTitle = computed(() => {
  if (!store.status) return t('common.loading')
  if (store.status.isDevBuild) return t('updates.devBuild')
  if (store.available) {
    return t('updates.newVersion', {
      sha: store.status.latest?.shortSha ?? '?', build: store.status.latest?.buildNumber ?? '?',
    })
  }
  if (store.status.state === 'Failed') return t('updates.checkFailed')
  return t('updates.upToDate')
})

const cannotApplyReason = computed(() =>
  store.status?.isDevBuild
    ? t('updates.reasonDev')
    : t('updates.reasonHost')
)

const lastChecked = computed(() => {
  const at = store.status?.lastCheckedUtc
  return at ? fmtDateTime(at) : t('updates.neverChecked')
})

const versionCards = computed(() => [
  {
    title: t('updates.installed'),
    version: store.status?.current ?? null,
    emptyText: store.status?.isDevBuild
      ? t('updates.noVersionJson')
      : t('updates.unknown'),
  },
  {
    title: t('updates.published'),
    version: store.status?.latest ?? null,
    emptyText: t('updates.neverChecked'),
  },
])

function confirmApply() {
  const target = store.status?.latest
  const ok = confirm(t('updates.confirmApply', {
    sha: target?.shortSha ?? '?', build: target?.buildNumber ?? '?',
  }))
  if (ok) void store.apply()
}
</script>

<style scoped>
.updates-page { display: flex; flex-direction: column; gap: 20px; }

.section {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 18px 20px;
}

.section-title {
  font-size: 13px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.6px;
  color: var(--text-muted);
  margin: 0 0 14px;
}

.status-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}
.status-head .section-title { margin-bottom: 14px; }

/* --- Status card --- */
.status-card {
  display: flex;
  align-items: center;
  gap: 14px;
  padding: 16px;
  border-radius: 8px;
  border: 1px solid var(--border);
  background: var(--bg-tertiary);
}
.status-card.accent  { border-color: color-mix(in srgb, var(--accent) 45%, transparent); }
.status-card.danger  { border-color: color-mix(in srgb, var(--danger) 45%, transparent); }

.status-icon { flex-shrink: 0; }
.status-icon.accent  { color: var(--accent); }
.status-icon.success { color: var(--success); }
.status-icon.danger  { color: var(--danger); }
.status-icon.muted   { color: var(--text-muted); }

.status-body { flex: 1; min-width: 0; }
.status-title { margin: 0; font-size: 15px; font-weight: 600; color: var(--text-primary); }
.status-msg {
  margin: 4px 0 0;
  font-size: 13px;
  color: var(--text-muted);
  word-break: break-word;
}
.status-msg.danger { color: var(--danger); }

/* --- Progress --- */
.progress-wrap { margin-top: 14px; }
.progress-track {
  height: 6px;
  border-radius: 3px;
  background: var(--bg-tertiary);
  overflow: hidden;
}
.progress-fill {
  height: 100%;
  background: var(--accent);
  border-radius: 3px;
  transition: width 0.3s ease;
}
/* Steps without a byte count (verify/extract) still need to look alive. */
.progress-fill.indeterminate {
  width: 35%;
  animation: slide 1.2s ease-in-out infinite;
}
@keyframes slide {
  0%   { margin-left: -35%; }
  100% { margin-left: 100%; }
}
.progress-label {
  display: inline-block;
  margin-top: 8px;
  font-size: 12px;
  color: var(--text-muted);
}

.restart-note {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 12px 0 0;
  font-size: 13px;
  color: var(--accent);
}

/* --- Versions --- */
.version-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(240px, 1fr));
  gap: 12px;
}

.version-card {
  border: 1px solid var(--border);
  border-radius: 8px;
  padding: 14px;
  background: var(--bg-tertiary);
}

.vc-title {
  margin: 0 0 10px;
  font-size: 12px;
  font-weight: 700;
  text-transform: uppercase;
  letter-spacing: 0.5px;
  color: var(--text-muted);
}
.vc-list { margin: 0; display: flex; flex-direction: column; gap: 6px; }
.vc-row { display: flex; gap: 10px; align-items: baseline; font-size: 13px; }
.vc-row dt { flex: 0 0 58px; color: var(--text-muted); font-size: 12px; }
.vc-row dd { margin: 0; color: var(--text-primary); min-width: 0; }
/* Commit subjects are arbitrary length — never let one widen the grid. */
.vc-row.subject dd {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.vc-empty { margin: 0; font-size: 13px; color: var(--text-muted); }

.running-hint { margin: 0 0 12px; font-size: 12px; line-height: 1.5; color: var(--text-muted); }
.running-warn { margin: 10px 0 0; font-size: 12px; color: var(--warning, #d08a00); }

.meta {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 10px 16px;
  margin: 16px 0 0;
  padding-top: 14px;
  border-top: 1px solid var(--border);
}
.meta div { display: flex; flex-direction: column; gap: 3px; }
.meta dt { font-size: 11px; text-transform: uppercase; letter-spacing: 0.5px; color: var(--text-muted); }
.meta dd { margin: 0; font-size: 13px; color: var(--text-primary); }

/* --- Notice --- */
.notice { display: flex; gap: 12px; align-items: flex-start; }
.notice-icon { flex-shrink: 0; margin-top: 2px; }
.notice-icon.warning { color: var(--warning); }
.notice-icon.muted   { color: var(--text-muted); }
.notice-title { margin: 0; font-size: 14px; font-weight: 600; color: var(--text-primary); }
.notice-sub   { margin: 4px 0 0; font-size: 13px; color: var(--text-muted); line-height: 1.6; }

code {
  font-family: ui-monospace, "SFMono-Regular", Menlo, monospace;
  font-size: 0.92em;
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 4px;
  padding: 1px 5px;
}

/* --- Buttons --- */
.btn-accent, .btn-ghost {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  padding: 8px 14px;
  border-radius: 6px;
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
  white-space: nowrap;
  transition: background 0.15s, opacity 0.15s;
}
.btn-accent {
  background: var(--accent);
  border: 1px solid var(--accent);
  color: #fff;
}
.btn-ghost {
  background: transparent;
  border: 1px solid var(--border);
  color: var(--text-muted);
}
.btn-ghost:hover:not(:disabled) { background: var(--bg-tertiary); color: var(--text-primary); }
.btn-accent:disabled, .btn-ghost:disabled { opacity: 0.5; cursor: not-allowed; }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
