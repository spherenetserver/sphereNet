<template>
  <div class="layout">
    <Sidebar />
    <div class="main">
      <TopBar />
      <div class="content">
        <RouterView />
      </div>
    </div>
  </div>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { RouterView } from 'vue-router'
import Sidebar from '@/components/Sidebar.vue'
import TopBar from '@/components/TopBar.vue'
import { startConnection, stopConnection, onLogBatch, onStatsUpdate, getConnection, retryPolicy } from '@/lib/signalr'
import { useAuthStore } from '@/stores/auth'
import { useLogsStore } from '@/stores/logs'
import { useServerStore } from '@/stores/server'

const auth   = useAuthStore()
const logs   = useLogsStore()
const server = useServerStore()

// False once the layout is gone: a pending retry must not open a connection for a
// page nobody is looking at.
let active = true
let attempt = 0

/** Start the hub, and keep trying while the layout is up and the session valid.
 *  Covers the two cases automatic reconnect never sees: the first start failing
 *  (Host still booting) and a connection the client has given up on (onclose). */
async function connect() {
  while (active && auth.loggedIn) {
    try {
      await startConnection()
      attempt = 0
      server.setConnected(true)
      return
    } catch {
      server.setConnected(false)
      const delay = retryPolicy.nextRetryDelayInMilliseconds({
        previousRetryCount: attempt++, elapsedMilliseconds: 0, retryReason: new Error('start failed'),
      }) ?? 15_000
      await new Promise(resolve => setTimeout(resolve, Math.max(delay, 1_000)))
    }
  }
}

onMounted(() => {
  // Handlers first: they belong to the connection object, whichever start wins.
  onLogBatch(batch => logs.addBatch(batch))
  onStatsUpdate(stats => server.updateStats(stats))

  const conn = getConnection()
  conn.onreconnected(() => server.setConnected(true))
  conn.onreconnecting(() => server.setConnected(false))
  conn.onclose(() => {
    server.setConnected(false)
    if (active) void connect()
  })

  void connect()
})

onUnmounted(async () => {
  active = false
  await stopConnection()
  server.setConnected(false)
})
</script>

<style scoped>
.layout {
  display: flex;
  height: 100vh;
  overflow: hidden;
  background-color: var(--bg-primary);
}

.main {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.content {
  flex: 1;
  overflow-y: auto;
  padding: 24px;
}
</style>
