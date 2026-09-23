<template>
  <div>
    <div class="toolbar">
      <input v-model="search" class="search-input" :placeholder="t('accounts.search')" />
      <button class="btn-accent" @click="openCreate">
        <Plus :size="15" /> {{ t('accounts.new') }}
      </button>
    </div>

    <div v-if="actionError" class="error-banner">
      <span>{{ actionError }}</span>
      <button class="icon-btn" :title="t('common.dismiss')" @click="actionError = ''"><X :size="14" /></button>
    </div>

    <div class="table-wrap">
      <table class="table" v-if="filtered.length > 0">
        <thead>
          <tr>
            <th>{{ t('accounts.colName') }}</th>
            <th>{{ t('accounts.colPrivLevel') }}</th>
            <th>{{ t('accounts.colStatus') }}</th>
            <th>{{ t('accounts.colLastIp') }}</th>
            <th>{{ t('accounts.colLastLogin') }}</th>
            <th>{{ t('accounts.colChars') }}</th>
            <th></th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="a in filtered" :key="a.name">
            <td class="bold">{{ a.name }}</td>
            <td>
              <span class="badge" :class="`plevel-${a.privLevel}`">{{ privLabel(a.privLevel) }}</span>
            </td>
            <td>
              <span class="badge" :class="a.isBanned ? 'banned' : 'active'">
                {{ a.isBanned ? t('accounts.banned') : t('accounts.active') }}
              </span>
            </td>
            <td class="mono text-muted">{{ a.lastIp || '—' }}</td>
            <td class="text-muted">{{ fmtDate(a.lastLogin) }}</td>
            <td>{{ a.charCount }}</td>
            <td class="actions">
              <button class="icon-btn" :title="t('accounts.edit')" :disabled="pending.has(a.name)" @click="openEdit(a)">
                <Pencil :size="15" />
              </button>
              <button class="icon-btn" :title="a.isBanned ? t('accounts.unban') : t('accounts.ban')" :disabled="pending.has(a.name)"
                @click="toggleBan(a)">
                <component :is="a.isBanned ? ShieldCheck : ShieldOff" :size="15" />
              </button>
              <button class="icon-btn danger" :title="t('accounts.delete')" :disabled="pending.has(a.name)"
                @click="confirmDelete(a.name)">
                <Trash2 :size="15" />
              </button>
            </td>
          </tr>
        </tbody>
      </table>
      <div v-else-if="isPending" class="empty">
        <Loader2 :size="32" class="empty-icon spin" />
        <p>{{ t('accounts.loading') }}</p>
      </div>
      <div v-else-if="isError && accounts.length === 0" class="empty">
        <AlertTriangle :size="32" class="empty-icon error-icon" />
        <p>{{ t('accounts.loadError', { error: loadError }) }}</p>
        <button class="btn-ghost" @click="refetch()">{{ t('common.retry') }}</button>
      </div>
      <div v-else-if="accounts.length > 0" class="empty">
        <Search :size="32" class="empty-icon" />
        <p>{{ t('accounts.noMatch', { q: search.trim() }) }}</p>
      </div>
      <div v-else class="empty">
        <UserCog :size="32" class="empty-icon" />
        <p>{{ t('accounts.none') }}</p>
      </div>
    </div>
    <p v-if="isError && accounts.length > 0" class="error-msg stale-note">
      {{ t('common.staleList', { error: loadError }) }}
    </p>

    <!-- Create account modal -->
    <div v-if="showCreate" class="modal-overlay" @click.self="showCreate = false">
      <div class="modal">
        <h3>{{ t('accounts.createTitle') }}</h3>
        <div class="field">
          <label>{{ t('accounts.username') }}</label>
          <input v-model="newName" :placeholder="t('accounts.usernamePlaceholder')" />
        </div>
        <div class="field">
          <label>{{ t('accounts.password') }}</label>
          <input v-model="newPass" type="password" :placeholder="t('accounts.passwordPlaceholder')" @keyup.enter="createAccount" />
        </div>
        <p v-if="createError" class="error-msg">{{ createError }}</p>
        <div class="modal-actions">
          <button class="btn-ghost" @click="showCreate = false">{{ t('common.cancel') }}</button>
          <button class="btn-accent" :disabled="creating || !newName.trim() || !newPass" @click="createAccount">
            {{ t('accounts.create') }}
          </button>
        </div>
      </div>
    </div>

    <!-- Edit account modal -->
    <div v-if="editing" class="modal-overlay" @click.self="closeEdit">
      <div class="modal">
        <h3>{{ t('accounts.editTitle', { name: editing.name }) }}</h3>
        <div class="field">
          <label>{{ t('accounts.privLevel') }}</label>
          <select v-model.number="editLevel">
            <option v-for="lvl in privLevels" :key="lvl" :value="lvl">{{ lvl }} — {{ privLabel(lvl) }}</option>
          </select>
        </div>
        <div class="field">
          <label>{{ t('accounts.newPassword') }} <span class="hint">{{ t('accounts.keepHint') }}</span></label>
          <input v-model="editPass" type="password" :placeholder="t('accounts.newPasswordPlaceholder')" autocomplete="new-password" />
        </div>
        <div v-if="editPass" class="field">
          <label>{{ t('accounts.confirmPassword') }}</label>
          <input v-model="editPass2" type="password" :placeholder="t('accounts.confirmPasswordPlaceholder')" autocomplete="new-password"
            @keyup.enter="saveEdit" />
        </div>
        <p v-if="editError" class="error-msg">{{ editError }}</p>
        <div class="modal-actions">
          <button class="btn-ghost" @click="closeEdit">{{ t('common.cancel') }}</button>
          <button class="btn-accent" :disabled="saving || !editChanged" @click="saveEdit">{{ t('common.save') }}</button>
        </div>
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { ref, computed } from 'vue'
import {
  Plus, ShieldOff, ShieldCheck, Trash2, UserCog, Pencil, Search, Loader2, AlertTriangle, X,
} from 'lucide-vue-next'
import { useQuery, useQueryClient } from '@tanstack/vue-query'
import { accountsApi, errorMessage, type AccountInfo } from '@/lib/api'
import { t, fmtDate as fmtLocaleDate } from '@/i18n'

const qc = useQueryClient()
const { data, isPending, isError, error, refetch } = useQuery({
  queryKey: ['accounts'],
  queryFn: () => accountsApi.list().then(r => r.data),
  refetchInterval: 10_000,
})

const accounts  = computed(() => data.value ?? [])
const loadError = computed(() => errorMessage(error.value))
const search    = ref('')
const filtered  = computed(() => {
  const q = search.value.trim().toLowerCase()
  if (!q) return accounts.value
  return accounts.value.filter(a => a.name.toLowerCase().includes(q))
})

const actionError = ref('')
// Accounts with a request in flight, so a double click can't fire twice.
const pending = ref(new Set<string>())

async function run(name: string, what: string, fn: () => Promise<unknown>) {
  actionError.value = ''
  pending.value.add(name)
  try {
    await fn()
  } catch (e) {
    actionError.value = t('accounts.actionFailed', { action: what, name, error: errorMessage(e) })
  } finally {
    pending.value.delete(name)
    qc.invalidateQueries({ queryKey: ['accounts'] })
  }
}

// --- Create ---
const showCreate  = ref(false)
const creating    = ref(false)
const newName     = ref('')
const newPass     = ref('')
const createError = ref('')

function openCreate() {
  createError.value = ''
  showCreate.value = true
}

async function createAccount() {
  if (!newName.value.trim() || !newPass.value) return
  createError.value = ''
  creating.value = true
  try {
    await accountsApi.create(newName.value.trim(), newPass.value)
    showCreate.value = false
    newName.value = newPass.value = ''
    qc.invalidateQueries({ queryKey: ['accounts'] })
  } catch (e: unknown) {
    createError.value = errorMessage(e, t('accounts.createFailed'))
  } finally {
    creating.value = false
  }
}

function toggleBan(a: AccountInfo) {
  return a.isBanned
    ? run(a.name, t('accounts.unban'), () => accountsApi.unban(a.name))
    : run(a.name, t('accounts.ban'), () => accountsApi.ban(a.name))
}

function confirmDelete(name: string) {
  if (!confirm(t('accounts.confirmDelete', { name }))) return
  return run(name, t('accounts.delete'), () => accountsApi.delete(name))
}

// --- Edit (privilege level + password) ---
const editing   = ref<AccountInfo | null>(null)
const editLevel = ref(1)
const editPass  = ref('')
const editPass2 = ref('')
const editError = ref('')
const saving    = ref(false)

const editChanged = computed(() =>
  !!editing.value && (editLevel.value !== editing.value.privLevel || editPass.value.length > 0))

function openEdit(a: AccountInfo) {
  editing.value   = a
  editLevel.value = a.privLevel
  editPass.value  = editPass2.value = ''
  editError.value = ''
}

function closeEdit() {
  editing.value = null
  editPass.value = editPass2.value = ''
}

async function saveEdit() {
  const acc = editing.value
  if (!acc || !editChanged.value) return
  if (editPass.value && !editPass.value.trim()) {
    editError.value = t('accounts.passwordBlank')
    return
  }
  if (editPass.value && editPass.value !== editPass2.value) {
    editError.value = t('accounts.passwordMismatch')
    return
  }
  editError.value = ''
  saving.value = true
  const done: string[] = []
  try {
    if (editLevel.value !== acc.privLevel) {
      await accountsApi.setPrivLevel(acc.name, editLevel.value)
      done.push(t('accounts.savedPrivLevel'))
    }
    if (editPass.value) {
      await accountsApi.setPassword(acc.name, editPass.value)
      done.push(t('accounts.savedPassword'))
    }
    closeEdit()
  } catch (e) {
    // The two calls are independent; say which part already went through.
    editError.value = [errorMessage(e, t('accounts.saveFailed')), ...done].join(' ')
  } finally {
    saving.value = false
    qc.invalidateQueries({ queryKey: ['accounts'] })
  }
}

// Matches SphereNet.Core.Enums.PrivLevel.
const privLabels: Record<number, string> = {
  0: 'Guest', 1: 'Player', 2: 'Counselor', 3: 'Seer',
  4: 'GM', 5: 'Dev', 6: 'Admin', 7: 'Owner',
}
const privLevels = [0, 1, 2, 3, 4, 5, 6, 7]

function privLabel(n: number): string { return privLabels[n] ?? `L${n}` }

function fmtDate(s: string): string {
  if (!s || s.startsWith('0001')) return '—'
  return fmtLocaleDate(s)
}
</script>

<style scoped>
.toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 16px;
  gap: 12px;
}

.search-input {
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 13px;
  padding: 8px 12px;
  outline: none;
  width: 220px;
}

.search-input:focus { border-color: var(--accent); }

.btn-accent {
  display: flex; align-items: center; gap: 6px;
  background: var(--accent); color: #0d1117;
  border: none; border-radius: 6px;
  font-size: 13px; font-weight: 600; padding: 8px 14px;
  cursor: pointer; transition: background 0.15s;
}

.btn-accent:hover:not(:disabled) { background: var(--accent-hover); }
.btn-accent:disabled { opacity: 0.5; cursor: not-allowed; }

.btn-ghost {
  padding: 8px 14px; border-radius: 6px; border: 1px solid var(--border);
  background: transparent; color: var(--text-muted);
  font-size: 13px; font-weight: 500; cursor: pointer; transition: all 0.15s;
}

.btn-ghost:hover { background: var(--bg-tertiary); color: var(--text-primary); }

.error-banner {
  display: flex; align-items: center; justify-content: space-between; gap: 12px;
  margin-bottom: 12px; padding: 8px 12px; border-radius: 6px;
  background: rgba(248,81,73,0.1); border: 1px solid var(--danger);
  color: var(--danger); font-size: 13px;
}

.table-wrap {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 10px; overflow: hidden;
}

.table { width: 100%; border-collapse: collapse; font-size: 13px; }

.table thead th {
  text-align: left; padding: 12px 16px;
  font-size: 11px; font-weight: 600; text-transform: uppercase;
  letter-spacing: 0.05em; color: var(--text-muted); border-bottom: 1px solid var(--border);
}

.table tbody td { padding: 11px 16px; border-bottom: 1px solid var(--border); color: var(--text-primary); }
.table tbody tr:last-child td { border-bottom: none; }
.table tbody tr:hover { background: rgba(255,255,255,0.02); }

.bold { font-weight: 600; }
.mono { font-family: 'Courier New', monospace; font-size: 12px; }
.text-muted { color: var(--text-muted); }

.badge {
  display: inline-block; padding: 2px 8px; border-radius: 9999px;
  font-size: 11px; font-weight: 600;
}

.active  { background: rgba(63,185,80,0.15);  color: var(--success); }
.banned  { background: rgba(248,81,73,0.15);   color: var(--danger); }
.plevel-0 { background: rgba(139,148,158,0.15); color: var(--text-muted); }
.plevel-1 { background: rgba(88,166,255,0.1);  color: var(--accent); }
.plevel-2, .plevel-3 { background: rgba(63,185,80,0.12); color: var(--success); }
.plevel-4, .plevel-5, .plevel-6, .plevel-7 { background: rgba(210,153,34,0.15); color: var(--warning); }

.actions { display: flex; gap: 6px; }

.icon-btn {
  display: flex; align-items: center; justify-content: center;
  width: 28px; height: 28px; border-radius: 6px;
  border: 1px solid var(--border); background: transparent;
  color: var(--text-muted); cursor: pointer; transition: all 0.15s;
}

.icon-btn:hover:not(:disabled) { background: var(--bg-tertiary); color: var(--text-primary); }
.icon-btn.danger:hover:not(:disabled) { border-color: var(--danger); color: var(--danger); }
.icon-btn:disabled { opacity: 0.4; cursor: not-allowed; }
.error-banner .icon-btn { width: 24px; height: 24px; border-color: transparent; color: var(--danger); }

.empty {
  display: flex; flex-direction: column; align-items: center;
  gap: 12px; padding: 64px; color: var(--text-muted);
}

.empty-icon { opacity: 0.3; }
.error-icon { color: var(--danger); opacity: 0.8; }
.empty p { margin: 0; font-size: 14px; }

.modal-overlay {
  position: fixed; inset: 0; background: rgba(0,0,0,0.6);
  display: flex; align-items: center; justify-content: center; z-index: 100;
}

.modal {
  background: var(--bg-secondary); border: 1px solid var(--border);
  border-radius: 12px; padding: 28px; width: 400px; max-width: calc(100vw - 32px);
  display: flex; flex-direction: column; gap: 16px;
}

.modal h3 { margin: 0; font-size: 16px; font-weight: 600; }

.field { display: flex; flex-direction: column; gap: 6px; }
.field label { font-size: 12px; font-weight: 500; color: var(--text-muted); }
.field .hint { font-weight: 400; opacity: 0.8; }
.field input,
.field select {
  background: var(--bg-tertiary); border: 1px solid var(--border);
  border-radius: 6px; color: var(--text-primary); font-size: 14px; padding: 9px 12px; outline: none;
}
.field input:focus,
.field select:focus { border-color: var(--accent); }

.error-msg { font-size: 13px; color: var(--danger); margin: 0; }
.stale-note { margin-top: 8px; }

.modal-actions { display: flex; justify-content: flex-end; gap: 8px; }

.spin { animation: spin 1s linear infinite; }
@keyframes spin { to { transform: rotate(360deg); } }
</style>
