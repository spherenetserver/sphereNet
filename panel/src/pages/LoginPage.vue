<template>
  <div class="login-wrap">
    <LanguageSwitch class="corner-lang" />
    <div class="login-box">
      <div class="login-brand">
        <span class="brand-icon">⚔</span>
        <span class="brand-name">SphereNet</span>
      </div>
      <p class="login-sub">{{ t('login.subtitle') }}</p>

      <form class="login-form" @submit.prevent="submit">
        <div class="field">
          <label>{{ t('login.password') }}</label>
          <input
            v-model="password"
            type="password"
            :placeholder="t('login.passwordPlaceholder')"
            autocomplete="current-password"
            :disabled="loading"
          />
          <p v-if="prefilled" class="hint-msg">
            {{ t('login.prefilled') }}
          </p>
        </div>

        <button type="submit" class="btn-primary" :disabled="loading">
          <span v-if="loading">{{ t('login.connecting') }}</span>
          <span v-else>{{ t('login.submit') }}</span>
        </button>

        <p v-if="error" class="error-msg">{{ error }}</p>
      </form>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { authApi } from '@/lib/api'
import { useAuthStore } from '@/stores/auth'
import { t } from '@/i18n'
import LanguageSwitch from '@/components/LanguageSwitch.vue'

const auth     = useAuthStore()
const password = ref('')
const loading  = ref(false)
const error    = ref('')
const prefilled = ref(false)

onMounted(async () => {
  try {
    const { data } = await authApi.localHint()
    if (data.password) {
      password.value  = data.password
      prefilled.value = true
    }
  } catch { /* no hint available — operator types the password */ }
})

async function submit() {
  if (!password.value) return
  error.value   = ''
  loading.value = true
  try {
    await auth.login(password.value)
  } catch (e: unknown) {
    const status = (e as { response?: { status?: number } }).response?.status
    error.value = status === 401
      ? t('login.wrongPassword')
      : status === 400
        ? t('login.notConfigured')
        : t('login.unreachable')
  } finally {
    loading.value = false
  }
}
</script>

<style scoped>
.corner-lang { position: fixed; top: 16px; right: 16px; }

.login-wrap {
  min-height: 100vh;
  display: flex;
  align-items: center;
  justify-content: center;
  background: var(--bg-primary);
}

.login-box {
  width: 360px;
  background: var(--bg-secondary);
  border: 1px solid var(--border);
  border-radius: 12px;
  padding: 40px;
}

.login-brand {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 4px;
}

.brand-icon { font-size: 24px; }

.brand-name {
  font-size: 20px;
  font-weight: 700;
  color: var(--text-primary);
}

.login-sub {
  color: var(--text-muted);
  font-size: 14px;
  margin: 0 0 32px;
}

.login-form { display: flex; flex-direction: column; gap: 16px; }

.field { display: flex; flex-direction: column; gap: 6px; }

.field label {
  font-size: 13px;
  font-weight: 500;
  color: var(--text-muted);
}

.field input {
  background: var(--bg-tertiary);
  border: 1px solid var(--border);
  border-radius: 6px;
  color: var(--text-primary);
  font-size: 14px;
  padding: 10px 12px;
  outline: none;
  transition: border-color 0.15s;
}

.field input:focus { border-color: var(--accent); }
.field input:disabled { opacity: 0.5; }

.btn-primary {
  background: var(--accent);
  color: #0d1117;
  border: none;
  border-radius: 6px;
  font-size: 14px;
  font-weight: 600;
  padding: 11px;
  cursor: pointer;
  transition: background 0.15s;
}

.btn-primary:hover:not(:disabled) { background: var(--accent-hover); }
.btn-primary:disabled { opacity: 0.5; cursor: not-allowed; }

.hint-msg {
  font-size: 12px;
  color: var(--text-muted);
  margin: 0;
}

.error-msg {
  font-size: 13px;
  color: var(--danger);
  margin: 0;
  text-align: center;
}
</style>
