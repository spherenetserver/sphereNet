<template>
  <div class="plugins-page">
    <div class="toolbar">
      <div class="toolbar-left">
        <span class="count">{{ t('plugins.count', { n: rows.length }) }}</span>
        <span class="badge" :class="overview?.bridgeListening ? 'on' : 'off'">
          {{ overview?.bridgeListening
            ? t('plugins.bridgeOn', { endpoint: overview.bridgeEndpoint })
            : t('plugins.bridgeOff') }}
        </span>
      </div>
      <button class="btn-ghost" :disabled="isFetching" @click="refetch()">
        <RefreshCw :size="15" :class="{ spin: isFetching }" /> {{ t('common.refresh') }}
      </button>
    </div>

    <div class="table-wrap">
      <table class="table" v-if="rows.length > 0">
        <thead>
          <tr>
            <th>{{ t('plugins.colName') }}</th>
            <th>{{ t('plugins.colKind') }}</th>
            <th>{{ t('plugins.colStatus') }}</th>
            <th>{{ t('plugins.colEvents') }}</th>
            <th>{{ t('plugins.colHooks') }}</th>
            <th>{{ t('plugins.colFaults') }}</th>
            <th>{{ t('plugins.colQueue') }}</th>
            <th>{{ t('plugins.colDetail') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="p in rows" :key="p.kind + p.name">
            <td class="bold">{{ p.name }}</td>
            <td>{{ p.kind === 'bridge' ? t('plugins.kindBridge') : t('plugins.kindDll') }}</td>
            <td>
              <span class="badge" :class="p.enabled ? 'on' : 'off'">
                {{ p.enabled ? t('plugins.statusOn') : t('plugins.statusOff') }}
              </span>
            </td>
            <td>{{ p.subscriptions }}</td>
            <td class="text-muted">
              {{ p.kind === 'dll' ? t('plugins.hooks', { packets: p.packetFilters, logins: p.loginChecks }) : '—' }}
            </td>
            <td :class="{ warn: p.faults > 0 || p.slowHooks > 0 || p.droppedEvents > 0 }">
              {{ p.kind === 'dll'
                ? t('plugins.faults', { faults: p.faults, slow: p.slowHooks })
                : t('plugins.dropped', { n: p.droppedEvents }) }}
            </td>
            <td>{{ p.queued }}</td>
            <td class="mono text-muted">
              {{ p.detail }}<template v-if="p.connectedUtc"> · {{ t('plugins.since', { time: fmtDateTime(p.connectedUtc) }) }}</template>
            </td>
          </tr>
        </tbody>
      </table>

      <div v-else-if="isPending" class="empty">
        <Loader2 :size="32" class="empty-icon spin" />
        <p>{{ t('common.loading') }}</p>
      </div>
      <div v-else-if="isError" class="empty">
        <AlertTriangle :size="32" class="empty-icon error-icon" />
        <p>{{ loadError }}</p>
      </div>
      <div v-else class="empty">
        <Puzzle :size="32" class="empty-icon" />
        <p>{{ t('plugins.empty') }}</p>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { computed } from 'vue'
import { RefreshCw, Loader2, AlertTriangle, Puzzle } from 'lucide-vue-next'
import { useQuery } from '@tanstack/vue-query'
import { pluginsApi, errorMessage } from '@/lib/api'
import { t } from '@/i18n'

const { data, isPending, isError, error, isFetching, refetch } = useQuery({
  queryKey: ['plugins'],
  queryFn: () => pluginsApi.overview().then(r => r.data),
  refetchInterval: 5000,
})

const overview  = computed(() => data.value)
const rows      = computed(() => data.value?.plugins ?? [])
const loadError = computed(() => errorMessage(error.value))

function fmtDateTime(iso: string): string {
  return new Date(iso).toLocaleString()
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

.count { font-size: 14px; font-weight: 600; color: var(--text-muted); }

.btn-ghost {
  display: flex; align-items: center; gap: 6px;
  padding: 6px 12px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); font-size: 13px; font-weight: 500;
  cursor: pointer; transition: all 0.15s;
}
.btn-ghost:hover { background: var(--bg-tertiary); color: var(--text-primary); }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }

.table-wrap {
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 10px;
  overflow-x: auto;
}

.table { width: 100%; border-collapse: collapse; font-size: 13px; }

.table thead th {
  text-align: left; padding: 12px 16px;
  font-size: 11px; font-weight: 600; text-transform: uppercase; letter-spacing: 0.05em;
  color: var(--text-muted); border-bottom: 1px solid var(--border); white-space: nowrap;
}

.table tbody td {
  padding: 12px 16px; border-bottom: 1px solid var(--border);
  color: var(--text-primary); white-space: nowrap;
}

.table tbody tr:last-child td { border-bottom: none; }
.table tbody tr:hover { background: rgba(255,255,255,0.02); }

.bold { font-weight: 600; }
.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }
.warn { color: var(--warning); }

.badge {
  display: inline-block; padding: 2px 8px; border-radius: 9999px;
  font-size: 11px; font-weight: 600;
}
.badge.on  { background: rgba(63,185,80,0.12); color: var(--success); }
.badge.off { background: rgba(139,148,158,0.15); color: var(--text-muted); }

.empty {
  display: flex; flex-direction: column; align-items: center; justify-content: center;
  gap: 10px; padding: 48px 16px; color: var(--text-muted);
}
.empty-icon { opacity: 0.3; }
.error-icon { color: var(--danger); opacity: 0.8; }
.empty p { margin: 0; font-size: 14px; }
</style>
