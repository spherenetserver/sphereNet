import axios from 'axios'
import { useAuthStore } from '@/stores/auth'

export const api = axios.create({
  baseURL: '/api',
  timeout: 10_000,
})

api.interceptors.request.use(config => {
  const auth = useAuthStore()
  if (auth.token) {
    config.headers.Authorization = `Bearer ${auth.token}`
  }
  return config
})

api.interceptors.response.use(
  r => r,
  err => {
    const url = err.config?.url ?? ''
    if (err.response?.status === 401 && !url.startsWith('/auth/')) {
      const auth = useAuthStore()
      auth.clearSession()
    }
    return Promise.reject(err)
  }
)

// --- Types ---

export interface ServerStats {
  serverName: string
  uptime: string
  uptimeSeconds: number
  onlinePlayers: number
  totalChars: number
  totalItems: number
  totalSectors: number
  tickCount: number
  memoryMB: number
  accounts: number
  cpuPercent: number
  threadCount: number
  avgTickMs: number
  maxTickMs: number
  p50TickMs: number
  p95TickMs: number
  p99TickMs: number
  multicoreEnabled: boolean
  maps: MapStats[] | null
  /** ISO timestamp of the last finished world save; null before the first one. */
  lastSaveUtc: string | null
  /** Duration of the last save, in seconds. */
  lastSaveSeconds: number
  saveInProgress: boolean
  /** null until a save has finished at least once. */
  lastSaveOk: boolean | null
  saveCount: number
}

export interface MapStats {
  mapId: number
  chars: number
  items: number
  sectors: number
  activeSectors: number
  onlinePlayers: number
}

export interface PlayerInfo {
  serial: number
  charName: string
  accountName: string
  mapId: number
  x: number
  y: number
  ip: string
  privLevel: number
  clientVersion: string
  sessionSeconds: number
}

export interface ShutdownSchedule {
  pending: boolean
  restart: boolean
  dueUtc: string | null
}

export interface AccountInfo {
  name: string
  privLevel: number
  isBanned: boolean
  lastIp: string
  lastLogin: string
  createDate: string
  charCount: number
}

export interface DebugState {
  packetDebug: boolean
  scriptDebug: boolean
}

export interface ScriptFileInfo {
  name: string
  relativePath: string
  sizeBytes: number
  lastModified: string
}

export interface ScriptValidationResult {
  ok: boolean
  errors: string[]
}

export interface SetupConfig {
  serverName: string
  servPort: number
  adminPassword: string
  adminPanelPort: number
  tickSleepMode?: number
  debugPackets?: boolean
  scriptDebug?: boolean
}

export interface SetupStatus {
  done: boolean
  hasScripts: boolean
}

export interface BuildVersion {
  sha: string
  shortSha: string
  branch: string
  buildNumber: number
  builtAt: string
  runtime: string
  commitSubject: string
}

export type UpdateStateName =
  | 'Idle' | 'Checking' | 'Downloading' | 'Verifying'
  | 'Extracting' | 'Staged' | 'Applying' | 'Failed'

export interface UpdateStatus {
  current: BuildVersion | null
  latest: BuildVersion | null
  updateAvailable: boolean
  isDevBuild: boolean
  state: UpdateStateName
  progressPercent: number
  message: string | null
  lastCheckedUtc: string | null
  canApply: boolean
  busy: boolean
  repo: string
  channel: string
  runtime: string
}

/** What the running binary reports about itself (/server/version). Not the
 *  updater's {@link BuildVersion}, which describes a downloadable release. */
export interface RunningBuild {
  commit: string
  shortCommit: string
  branch: string
  /** true / false / null — null means the build was never told (not "clean"). */
  dirty: boolean | null
  assemblyVersion: string
  raw: string
  stamped: boolean
  expected: string | null
  upToDate: boolean | null
}

// --- API helpers ---

/** Joins path segments onto a route, URL-encoding each one. Account names and
 *  IPv6 addresses carry characters (space, '/', ':', '%', '#', '?') that would
 *  otherwise change which route the request hits. */
export function apiPath(base: string, ...segments: (string | number)[]): string {
  return [base, ...segments.map(s => encodeURIComponent(String(s)))].join('/')
}

/** Best human-readable message from a failed request: the backend's
 *  `{ error }` / `{ message }` / `{ detail }` body, else the HTTP status. */
export function errorMessage(e: unknown, fallback = 'Request failed'): string {
  const err = e as { response?: { status?: number; data?: unknown }; message?: string }
  const data = err?.response?.data
  if (typeof data === 'string' && data.trim()) return data
  if (data && typeof data === 'object') {
    const d = data as { error?: unknown; message?: unknown; detail?: unknown; title?: unknown }
    for (const v of [d.error, d.message, d.detail, d.title]) {
      if (typeof v === 'string' && v.trim()) return v
    }
  }
  if (err?.response?.status) return `${fallback} (HTTP ${err.response.status})`
  if (err?.message) return `${fallback}: ${err.message}`
  return fallback
}

export const serverApi = {
  status:    () => api.get<ServerStats>('/server/status'),
  running:   () => api.get<{ running: boolean }>('/server/running'),
  save:      () => api.post('/server/save'),
  shutdown:  () => api.post('/server/shutdown'),
  restart:     () => api.post('/server/restart'),
  startServer: () => api.post('/server/start'),
  resync:    () => api.post('/server/resync'),
  gc:        () => api.post('/server/gc'),
  respawn:   () => api.post('/server/respawn'),
  restock:   () => api.post('/server/restock'),
  broadcast: (message: string) => api.post('/server/broadcast', { message }),
  command:   (command: string) => api.post<{ lines: string[] }>('/server/command', { command }),
  // What the RUNNING binary says about itself, read from its own build stamp.
  // Deliberately separate from the updater's version metadata: those describe
  // what was downloaded, this describes what is actually executing, and the two
  // disagreeing is precisely the situation worth seeing.
  version:   (expected?: string) => api.get<RunningBuild>(
    expected ? `/server/version?expected=${encodeURIComponent(expected)}` : '/server/version'),
  // Delayed shutdown/restart with in-game countdown broadcasts. 409 when one is
  // already pending.
  schedule:       (seconds: number, restart: boolean, message?: string) =>
    api.post<ShutdownSchedule>('/server/schedule', message ? { seconds, restart, message } : { seconds, restart }),
  getSchedule:    () => api.get<ShutdownSchedule>('/server/schedule'),
  cancelSchedule: () => api.post<ShutdownSchedule>('/server/schedule/cancel'),
}

// Runtime-only block list: the server forgets it on restart.
export const ipBlocksApi = {
  list:   () => api.get<string[]>('/ipblocks'),
  add:    (ip: string) => api.post('/ipblocks', { ip }),
  remove: (ip: string) => api.delete(apiPath('/ipblocks', ip)),
}

export const playersApi = {
  list:       () => api.get<PlayerInfo[]>('/players'),
  disconnect: (serial: number) => api.post<{ message?: string }>(apiPath('/players', serial, 'disconnect')),
  message:    (serial: number, text: string) => api.post(apiPath('/players', serial, 'message'), { text }),
}

export const accountsApi = {
  list:        () => api.get<AccountInfo[]>('/accounts'),
  get:         (name: string) => api.get<AccountInfo>(apiPath('/accounts', name)),
  create:      (name: string, password: string) => api.post('/accounts', { name, password }),
  delete:      (name: string) => api.delete(apiPath('/accounts', name)),
  ban:         (name: string) => api.post(apiPath('/accounts', name, 'ban')),
  unban:       (name: string) => api.post(apiPath('/accounts', name, 'unban')),
  setPassword: (name: string, password: string) => api.put(apiPath('/accounts', name, 'password'), { password }),
  // PanelHost binds this body to ChangePlevelRequest(int Level) -> JSON "level".
  setPrivLevel:(name: string, level: number)    => api.put(apiPath('/accounts', name, 'plevel'), { level }),
}

export const authApi = {
  login:  (password: string) => api.post<{ token: string; serverName: string }>('/auth/login', { password }),
  // password is null unless AdminPanelAutoFill=1, the request is loopback, and
  // sphere.ini still holds the password in plaintext.
  localHint: () => api.get<{ password: string | null }>('/auth/local-hint'),
  logout: (token: string) => api.post('/auth/logout', null, {
    headers: { Authorization: `Bearer ${token}` },
  }),
}

export const setupApi = {
  needed:  () => api.get<{ needed: boolean }>('/setup/needed'),
  config:  () => api.get<SetupConfig>('/setup/config'),
  apply:   (cfg: SetupConfig) => api.post('/setup/apply', cfg),
  status:  () => api.get<SetupStatus>('/setup/status'),
}

export const settingsApi = {
  getDebug: () => api.get<DebugState>('/settings/debug'),
  setDebug: (state: DebugState) => api.post('/settings/debug', state),
}

export const updateApi = {
  status: () => api.get<UpdateStatus>('/update/status'),
  // The backend reaches out to GitHub here, so allow more than the 10s default.
  check:  () => api.post<UpdateStatus>('/update/check', null, { timeout: 60_000 }),
  // Returns 202 immediately; the download runs in the background and is
  // tracked by polling /update/status.
  apply:  () => api.post<UpdateStatus>('/update/apply'),
}

export const scriptsApi = {
  list:     () => api.get<ScriptFileInfo[]>('/scripts'),
  pack:     () => api.get<{ repo: string; branch: string; url: string }>('/scripts/pack'),
  content:  (path: string) => api.get<{ content: string }>(`/scripts/content`, { params: { path } }),
  validate: (path: string, content: string) => api.post<ScriptValidationResult>('/scripts/validate', { path, content }),
  save:     (path: string, content: string) => api.put<{ saved: boolean; path: string; validation: ScriptValidationResult }>('/scripts/content', { path, content }),
  download: () => api.post<{ filesInstalled: number; filesBackedUp: number; backupFolder: string | null }>('/scripts/download', null, { timeout: 120_000 }),
}
