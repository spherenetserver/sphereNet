<template>
  <aside class="sidebar">
    <div class="brand">
      <span class="brand-icon">⚔</span>
      <span class="brand-name">SphereNet</span>
    </div>

    <nav class="nav">
      <RouterLink
        v-for="item in navItems"
        :key="item.to"
        :to="item.to"
        class="nav-item"
        active-class="active"
      >
        <component :is="item.icon" :size="18" />
        <span>{{ item.label }}</span>
        <span v-if="item.to === '/updates' && updates.available" class="nav-badge" title="Yeni surum hazir" />
      </RouterLink>
    </nav>

    <div class="sidebar-footer">
      <button class="logout-btn" @click="auth.logout()">
        <LogOut :size="16" />
        <span>Logout</span>
      </button>
    </div>
  </aside>
</template>

<script setup lang="ts">
import { onMounted, onUnmounted } from 'vue'
import { RouterLink } from 'vue-router'
import {
  LayoutDashboard, Terminal, Users, UserCog, Server, LogOut, ScrollText, Settings,
  PanelTop, ArrowUpCircle,
} from 'lucide-vue-next'
import { useAuthStore } from '@/stores/auth'
import { useUpdateStore } from '@/stores/update'

const auth    = useAuthStore()
const updates = useUpdateStore()

// The sidebar is mounted for the whole authenticated session, which makes it
// the natural owner of the background update poll — the badge then appears
// without anyone opening the Updates page. It unmounts on logout, so stop the
// timer here too: the poll needs a bearer token and would otherwise keep firing
// 401s at a logged-out panel.
onMounted(() => void updates.start())
onUnmounted(() => updates.stopPolling())

const navItems = [
  { to: '/dashboard', label: 'Dashboard', icon: LayoutDashboard },
  { to: '/logs',      label: 'Console',   icon: Terminal        },
  { to: '/players',   label: 'Players',   icon: Users           },
  { to: '/accounts',  label: 'Accounts',  icon: UserCog         },
  { to: '/server',    label: 'Server',    icon: Server          },
  { to: '/scripts',   label: 'Scripts',   icon: ScrollText      },
  { to: '/gumps',     label: 'Gumps',     icon: PanelTop        },
  { to: '/updates',   label: 'Updates',   icon: ArrowUpCircle   },
  { to: '/settings',  label: 'Settings',  icon: Settings        },
]
</script>

<style scoped>
.sidebar {
  width: 220px;
  flex-shrink: 0;
  background: var(--bg-secondary);
  border-right: 1px solid var(--border);
  display: flex;
  flex-direction: column;
  padding: 16px 0;
}

.brand {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 0 20px 20px;
  border-bottom: 1px solid var(--border);
  margin-bottom: 8px;
}

.brand-icon { font-size: 20px; }

.brand-name {
  font-weight: 700;
  font-size: 15px;
  color: var(--text-primary);
  letter-spacing: 0.3px;
}

.nav {
  flex: 1;
  display: flex;
  flex-direction: column;
  padding: 8px;
  gap: 2px;
}

.nav-item {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 9px 12px;
  border-radius: 6px;
  color: var(--text-muted);
  text-decoration: none;
  font-size: 14px;
  font-weight: 500;
  transition: background 0.15s, color 0.15s;
}

.nav-item:hover { background: var(--bg-tertiary); color: var(--text-primary); }
.nav-item.active { background: var(--bg-tertiary); color: var(--accent); }

/* Unread-style dot: an update is waiting to be applied. */
.nav-badge {
  margin-left: auto;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: var(--accent);
  flex-shrink: 0;
  animation: badge-pulse 2s ease-in-out infinite;
}

@keyframes badge-pulse {
  0%, 100% { opacity: 1; }
  50%      { opacity: 0.35; }
}

.sidebar-footer {
  padding: 12px;
  border-top: 1px solid var(--border);
}

.logout-btn {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  padding: 8px 12px;
  border-radius: 6px;
  background: transparent;
  border: none;
  color: var(--text-muted);
  font-size: 14px;
  font-weight: 500;
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.logout-btn:hover { background: rgba(248, 81, 73, 0.1); color: var(--danger); }
</style>
