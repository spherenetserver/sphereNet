import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { updateApi, type UpdateStatus } from '@/lib/api'

/** Poll interval while nothing is happening — just keeps the sidebar badge fresh. */
const IDLE_POLL_MS = 60_000
/** Poll interval while a download/apply is running, so the progress bar moves. */
const BUSY_POLL_MS = 1_500

export const useUpdateStore = defineStore('update', () => {
  const status = ref<UpdateStatus | null>(null)
  /** False once /api/update/* 404s — sphere.ini has no AppUpdateRepo. */
  const supported = ref(true)
  const error = ref<string | null>(null)
  const checking = ref(false)
  /**
   * Set once the updater has been launched. The Host is about to exit and take
   * the panel with it, so from here on a failed request is expected, not an error.
   */
  const restarting = ref(false)

  let timer: ReturnType<typeof setTimeout> | null = null

  const available = computed(() => status.value?.updateAvailable === true)
  const busy = computed(() => status.value?.busy === true)

  function stopPolling() {
    if (timer !== null) {
      clearTimeout(timer)
      timer = null
    }
  }

  /** Self-rescheduling poll — interval adapts to whether work is in flight. */
  function schedule() {
    stopPolling()
    if (!supported.value || restarting.value) return
    timer = setTimeout(() => void poll(), busy.value ? BUSY_POLL_MS : IDLE_POLL_MS)
  }

  async function poll() {
    if (!supported.value || restarting.value) return
    try {
      const { data } = await updateApi.status()
      status.value = data
      error.value = null

      // The Host exits during this state; stop polling and wait for it to
      // come back instead of hammering a socket that is about to close.
      if (data.state === 'Applying') {
        beginRestartWatch()
        return
      }
    } catch (err: unknown) {
      if (isNotFound(err)) {
        // Updater not configured — hide the feature rather than showing an error.
        supported.value = false
        stopPolling()
        return
      }
      error.value = describe(err)
    }
    schedule()
  }

  async function start() {
    await poll()
  }

  async function check() {
    if (!supported.value || checking.value) return
    checking.value = true
    try {
      const { data } = await updateApi.check()
      status.value = data
      error.value = null
    } catch (err: unknown) {
      if (isNotFound(err)) {
        supported.value = false
        return
      }
      error.value = describe(err)
    } finally {
      checking.value = false
      schedule()
    }
  }

  async function apply() {
    if (!supported.value) return
    error.value = null
    try {
      const { data } = await updateApi.apply()
      status.value = data
      schedule()
    } catch (err: unknown) {
      error.value = describe(err)
    }
  }

  /**
   * The panel dies with the Host mid-update. Poll /health (which needs no auth)
   * until the updater has relaunched the Host, then hard-reload so the freshly
   * deployed panel assets are the ones running.
   */
  function beginRestartWatch() {
    if (restarting.value) return
    restarting.value = true
    stopPolling()

    const deadline = Date.now() + 5 * 60_000

    const probe = async () => {
      if (Date.now() > deadline) {
        restarting.value = false
        error.value =
          'Sunucu 5 dakika icinde geri gelmedi. logs/update.log dosyasini kontrol et.'
        schedule()
        return
      }
      try {
        const res = await fetch('/health', { cache: 'no-store' })
        if (res.ok) {
          // New assets are on disk now — a soft route change would keep the
          // old bundle alive.
          window.location.reload()
          return
        }
      } catch {
        // Expected while the Host is down.
      }
      setTimeout(() => void probe(), 2_000)
    }

    // Give the Host a moment to actually go down; probing immediately would
    // hit the still-alive old process and reload too early.
    setTimeout(() => void probe(), 5_000)
  }

  function isNotFound(err: unknown): boolean {
    return (err as { response?: { status?: number } })?.response?.status === 404
  }

  function describe(err: unknown): string {
    const e = err as { response?: { data?: { error?: string } }; message?: string }
    return e?.response?.data?.error ?? e?.message ?? 'Bilinmeyen hata'
  }

  return {
    status, supported, error, checking, restarting,
    available, busy,
    start, stopPolling, poll, check, apply,
  }
})
