<template>
  <div class="lang-switch" role="group" :aria-label="t('lang.label')">
    <button
      v-for="l in LOCALES"
      :key="l"
      type="button"
      :class="{ active: locale === l }"
      :aria-pressed="locale === l"
      :title="t(l === 'tr' ? 'lang.tr' : 'lang.en')"
      @click="setLocale(l)"
    >
      {{ l.toUpperCase() }}
    </button>
  </div>
</template>

<script setup lang="ts">
import { LOCALES, locale, setLocale, t } from '@/i18n'
</script>

<style scoped>
.lang-switch {
  display: inline-flex;
  border: 1px solid var(--border);
  border-radius: 6px;
  overflow: hidden;
}

.lang-switch button {
  padding: 3px 8px;
  border: none;
  background: transparent;
  color: var(--text-muted);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  cursor: pointer;
  transition: all 0.15s;
}

.lang-switch button + button { border-left: 1px solid var(--border); }
.lang-switch button:hover { color: var(--text-primary); background: var(--bg-tertiary); }
.lang-switch button.active { background: rgba(88,166,255,0.15); color: var(--accent); }
</style>
