<template>
  <div class="updates-page">

    <!-- Updater not configured — sphere.ini has no AppUpdateRepo -->
    <section v-if="!store.supported" class="section">
      <div class="notice">
        <PackageX :size="20" class="notice-icon muted" />
        <div>
          <p class="notice-title">Guncelleme sistemi yapilandirilmamis</p>
          <p class="notice-sub">
            <code>config/sphere.ini</code> icinde <code>APPUPDATEREPO</code> bos.
            Ornek: <code>APPUPDATEREPO=spherenetserver/sphereNet</code>
          </p>
        </div>
      </div>
    </section>

    <template v-else>
      <!-- Status -->
      <section class="section">
        <div class="status-head">
          <h2 class="section-title">Guncelleme Durumu</h2>
          <button class="btn-ghost" :disabled="store.checking || store.busy" @click="store.check()">
            <RefreshCw :size="15" :class="{ spin: store.checking }" />
            {{ store.checking ? 'Kontrol ediliyor…' : 'Check for update' }}
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
            <Download :size="15" /> Simdi guncelle
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
          Sunucu yeniden baslatiliyor — geri geldiginde bu sayfa kendiliginden tazelenecek.
        </p>
      </section>

      <!-- Versions -->
      <section class="section">
        <h2 class="section-title">Surumler</h2>
        <div class="version-grid">
          <div v-for="card in versionCards" :key="card.title" class="version-card">
            <p class="vc-title">{{ card.title }}</p>
            <dl v-if="card.version" class="vc-list">
              <div class="vc-row"><dt>Commit</dt><dd><code>{{ card.version.shortSha }}</code></dd></div>
              <div class="vc-row"><dt>Build</dt><dd>#{{ card.version.buildNumber }}</dd></div>
              <div class="vc-row"><dt>Dal</dt><dd><code>{{ card.version.branch }}</code></dd></div>
              <div class="vc-row"><dt>Tarih</dt><dd>{{ new Date(card.version.builtAt).toLocaleString() }}</dd></div>
              <div class="vc-row subject" :title="card.version.commitSubject">
                <dt>Konu</dt><dd>{{ card.version.commitSubject || '—' }}</dd>
              </div>
            </dl>
            <p v-else class="vc-empty">{{ card.emptyText }}</p>
          </div>
        </div>

        <dl class="meta">
          <div><dt>Depo</dt><dd><code>{{ store.status?.repo ?? '—' }}</code></dd></div>
          <div><dt>Kanal</dt><dd><code>{{ store.status?.channel ?? '—' }}</code></dd></div>
          <div><dt>Platform</dt><dd><code>{{ store.status?.runtime ?? '—' }}</code></dd></div>
          <div><dt>Son kontrol</dt><dd>{{ lastChecked }}</dd></div>
        </dl>
      </section>

      <!-- Why apply may be unavailable -->
      <section v-if="store.status && !store.status.canApply" class="section">
        <div class="notice">
          <Info :size="20" class="notice-icon warning" />
          <div>
            <p class="notice-title">Bu kurulumda guncelleme uygulanamaz</p>
            <p class="notice-sub">{{ cannotApplyReason }}</p>
          </div>
        </div>
      </section>
    </template>
  </div>
</template>

<script setup lang="ts">
import { computed, onMounted } from 'vue'
import {
  RefreshCw, Download, CheckCircle2, AlertTriangle, PackageX, Info, Loader, Sparkles,
} from 'lucide-vue-next'
import { useUpdateStore } from '@/stores/update'

const store = useUpdateStore()

// Refresh on entry so the page never opens on a minute-old snapshot. The
// Sidebar owns the long-lived poll, so this must not stop it on unmount —
// start() reuses the existing timer rather than stacking a second one.
onMounted(() => void store.start())

const progressPercent = computed(() => store.status?.progressPercent ?? 0)

const stateLabel = computed(() => ({
  Idle:       'Bekliyor',
  Checking:   'Surum bilgisi aliniyor',
  Downloading:'Indiriliyor',
  Verifying:  'SHA256 dogrulaniyor',
  Extracting: 'Paket aciliyor',
  Staged:     'Hazirlaniyor',
  Applying:   'Uygulaniyor',
  Failed:     'Basarisiz',
}[store.status?.state ?? 'Idle']))

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
  if (!store.status) return 'Yukleniyor…'
  if (store.status.isDevBuild) return 'Kaynaktan derlenmis kurulum'
  if (store.available) {
    return `Yeni surum hazir: ${store.status.latest?.shortSha} (build #${store.status.latest?.buildNumber})`
  }
  if (store.status.state === 'Failed') return 'Guncelleme kontrolu basarisiz'
  return 'Sunucu guncel'
})

const cannotApplyReason = computed(() =>
  store.status?.isDevBuild
    ? 'Bu kurulumda version.json yok — kaynaktan derlenmis demektir. Kaynak agacinda update.cmd kullan.'
    : 'Guncelleme yalnizca SphereNet.Host.exe uzerinden calisirken uygulanabilir.'
)

const lastChecked = computed(() => {
  const t = store.status?.lastCheckedUtc
  return t ? new Date(t).toLocaleString() : 'Henuz kontrol edilmedi'
})

const versionCards = computed(() => [
  {
    title: 'Kurulu',
    version: store.status?.current ?? null,
    emptyText: store.status?.isDevBuild
      ? 'version.json yok (kaynaktan derlenmis)'
      : 'Bilinmiyor',
  },
  {
    title: 'Yayindaki',
    version: store.status?.latest ?? null,
    emptyText: 'Henuz kontrol edilmedi',
  },
])

function confirmApply() {
  const target = store.status?.latest
  const ok = confirm(
    `${target?.shortSha} (build #${target?.buildNumber}) surumune guncellensin mi?\n\n` +
    'Dunya kaydedilecek, sunucu kapanacak, dosyalar degistirilecek ve sunucu ' +
    'yeniden baslatilacak. Oyundaki tum oyuncular baglantisini kaybeder.\n\n' +
    'config/, save/ ve scripts/ klasorlerine dokunulmaz.'
  )
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
