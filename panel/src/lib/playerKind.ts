/** The players page shows real players, player bots, or both. The choice is kept
 *  per browser; storage can be missing or throw, and then the page just starts on
 *  real players. */
export type PlayerKind = 'players' | 'bots' | 'all'

export const PLAYER_KIND_KEY = 'spherenet.players.kind'
export const PLAYER_KINDS: readonly PlayerKind[] = ['players', 'bots', 'all']

function isPlayerKind(v: unknown): v is PlayerKind {
  return typeof v === 'string' && (PLAYER_KINDS as readonly string[]).includes(v)
}

export function readPlayerKind(): PlayerKind {
  try {
    const v = localStorage.getItem(PLAYER_KIND_KEY)
    return isPlayerKind(v) ? v : 'players'
  } catch {
    return 'players'
  }
}

export function savePlayerKind(kind: PlayerKind): void {
  try {
    localStorage.setItem(PLAYER_KIND_KEY, kind)
  } catch {
    // Storage unavailable: the choice lasts for this page only.
  }
}

export function ofKind<T extends { isBot?: boolean }>(list: readonly T[], kind: PlayerKind): T[] {
  if (kind === 'all') return [...list]
  const bots = kind === 'bots'
  return list.filter(p => !!p.isBot === bots)
}

export function countKinds(list: readonly { isBot?: boolean }[]): { players: number; bots: number } {
  let bots = 0
  for (const p of list) if (p.isBot) bots++
  return { players: list.length - bots, bots }
}
