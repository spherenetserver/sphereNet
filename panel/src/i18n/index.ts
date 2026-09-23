import { ref } from 'vue'
import { en, type MessageKey, type Messages } from './en'
import { tr } from './tr'

export type { MessageKey, Messages }
export type Locale = 'tr' | 'en'
export type Params = Record<string, string | number>

export const LOCALES: readonly Locale[] = ['tr', 'en']
export const STORAGE_KEY = 'sn_locale'

const dictionaries: Record<Locale, Messages> = { en, tr }

function isLocale(v: unknown): v is Locale {
  return v === 'tr' || v === 'en'
}

/** The saved choice, or null when there is none / storage is unavailable. */
export function readSavedLocale(): Locale | null {
  try {
    const v = localStorage.getItem(STORAGE_KEY)
    return isLocale(v) ? v : null
  } catch {
    return null
  }
}

/** Saved choice first; otherwise Turkish for a Turkish browser, else English. */
export function detectLocale(saved: string | null, browserLanguage: string | undefined): Locale {
  if (isLocale(saved)) return saved
  return browserLanguage?.toLowerCase().startsWith('tr') ? 'tr' : 'en'
}

function browserLanguage(): string | undefined {
  try {
    return typeof navigator !== 'undefined' ? navigator.language : undefined
  } catch {
    return undefined
  }
}

function applyDocumentLang(l: Locale) {
  if (typeof document !== 'undefined') document.documentElement.lang = l
}

/** Current UI language. Module-level so `t()` also works outside components. */
export const locale = ref<Locale>(detectLocale(readSavedLocale(), browserLanguage()))
applyDocumentLang(locale.value)

/** Re-reads storage and the browser language (startup, and tests). */
export function initLocale(): Locale {
  locale.value = detectLocale(readSavedLocale(), browserLanguage())
  applyDocumentLang(locale.value)
  return locale.value
}

export function setLocale(l: Locale) {
  locale.value = l
  applyDocumentLang(l)
  try {
    localStorage.setItem(STORAGE_KEY, l)
  } catch {
    // Private mode / blocked storage: the choice just won't survive a reload.
  }
}

/** Replaces `{name}` placeholders; unknown ones are left as written. */
export function interpolate(template: string, params?: Params): string {
  if (!params) return template
  return template.replace(/\{(\w+)\}/g, (whole, name: string) =>
    Object.prototype.hasOwnProperty.call(params, name) ? String(params[name]) : whole)
}

export function t(key: MessageKey, params?: Params): string {
  const msg = dictionaries[locale.value][key] ?? en[key] ?? key
  return interpolate(msg, params)
}

/** BCP 47 tag for Intl formatting. */
export function localeTag(): string {
  return locale.value === 'tr' ? 'tr-TR' : 'en-US'
}

export function fmtNumber(n: number | undefined | null, opts?: Intl.NumberFormatOptions): string {
  if (n === undefined || n === null || !Number.isFinite(n)) return '—'
  return n.toLocaleString(localeTag(), opts)
}

export function fmtDateTime(v: string | number | Date): string {
  const d = new Date(v)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleString(localeTag())
}

export function fmtDate(v: string | number | Date): string {
  const d = new Date(v)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleDateString(localeTag())
}

export function fmtTime(v: string | number | Date, opts?: Intl.DateTimeFormatOptions): string {
  const d = new Date(v)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleTimeString(localeTag(), opts)
}

export function useI18n() {
  return { t, locale, setLocale, fmtNumber, fmtDate, fmtDateTime, fmtTime, localeTag }
}
