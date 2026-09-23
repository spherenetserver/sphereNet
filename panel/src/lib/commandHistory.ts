/** Recently run console commands, newest first, kept per browser. Storage can be
 *  missing or throw (private windows, blocked site data); the history then just
 *  lives for the page's lifetime. */
export const HISTORY_KEY = 'spherenet.console.history'
export const HISTORY_LIMIT = 20

export function readHistory(): string[] {
  try {
    const raw = localStorage.getItem(HISTORY_KEY)
    if (!raw) return []
    const parsed: unknown = JSON.parse(raw)
    if (!Array.isArray(parsed)) return []
    return parsed.filter((v): v is string => typeof v === 'string' && v.trim() !== '').slice(0, HISTORY_LIMIT)
  } catch {
    return []
  }
}

/** The history with `command` moved (or added) to the front, capped. */
export function pushHistory(history: readonly string[], command: string): string[] {
  const cmd = command.trim()
  if (!cmd) return [...history]
  return [cmd, ...history.filter(h => h !== cmd)].slice(0, HISTORY_LIMIT)
}

export function saveHistory(history: readonly string[]): void {
  try {
    localStorage.setItem(HISTORY_KEY, JSON.stringify(history.slice(0, HISTORY_LIMIT)))
  } catch {
    // Storage unavailable: keep the in-memory list only.
  }
}

export function clearHistory(): void {
  try {
    localStorage.removeItem(HISTORY_KEY)
  } catch {
    // ignore
  }
}
