import * as signalR from '@microsoft/signalr'
import { useAuthStore } from '@/stores/auth'
import type { ServerStats } from './api'

export interface LogEntry {
  timestamp: string
  level: string
  message: string
  source: string
}

let connection: signalR.HubConnection | null = null

/** Reconnect delays after a drop. The library default (0/2/10/30 s) gives up after
 *  the fourth try, and a laptop that slept a minute came back to a console that
 *  never updated again while every REST page still worked. This one keeps trying. */
const RETRY_DELAYS_MS = [0, 2_000, 5_000, 10_000]
const RETRY_STEADY_MS = 15_000

export const retryPolicy: signalR.IRetryPolicy = {
  nextRetryDelayInMilliseconds: ctx => RETRY_DELAYS_MS[ctx.previousRetryCount] ?? RETRY_STEADY_MS,
}

export function getConnection(): signalR.HubConnection {
  if (connection) return connection

  const auth = useAuthStore()

  connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/server', {
      accessTokenFactory: () => auth.token ?? '',
    })
    .withAutomaticReconnect(retryPolicy)
    .configureLogging(signalR.LogLevel.Warning)
    .build()

  return connection
}

export async function startConnection(): Promise<signalR.HubConnection> {
  const conn = getConnection()
  if (conn.state === signalR.HubConnectionState.Disconnected) {
    await conn.start()
  }
  return conn
}

export async function stopConnection() {
  if (connection) {
    await connection.stop()
    connection = null
  }
}

export function onLogBatch(handler: (entries: LogEntry[]) => void) {
  getConnection().on('ReceiveLogBatch', handler)
}

export function onStatsUpdate(handler: (stats: ServerStats) => void) {
  getConnection().on('StatsUpdate', handler)
}

export async function executeCommand(command: string): Promise<string[]> {
  const conn = getConnection()
  return conn.invoke<string[]>('ExecuteCommand', command)
}
