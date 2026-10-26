<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import AppIcon from './AppIcon.vue'
import { type AppIconName } from '../core/icons'
import Avatar from './Avatar.vue'
import { useAuthStore } from '../stores/auth'

// 一级导航（UI-01，计划 §2.1 / §5.1 / §16.5）：
// ≥1200：172px 图标+文字；801~1099：72px 图标栏（hover/focus 有 title 提示）；≤800 隐藏（移动端导航 UI-04）。
// 群聊入口本阶段没有群聊列表路由，灰显占位 + 「即将上线」（附录 A 登记），不提交请求。
interface NavItem {
  key: string
  label: string
  icon: AppIconName
  to?: string
}

const items: NavItem[] = [
  { key: 'chat', label: '消息', icon: 'chat', to: '/chat' },
  { key: 'contacts', label: '联系人', icon: 'contacts', to: '/contacts' },
  { key: 'groups', label: '群聊', icon: 'groups' },
  { key: 'settings', label: '设置', icon: 'settings', to: '/settings' },
]

const route = useRoute()
const auth = useAuthStore()

/** 当前一级导航高亮：按路由前缀判断（聊天详情仍在“消息”下）。 */
const activeKey = computed(() => {
  if (route.path.startsWith('/contacts')) return 'contacts'
  if (route.path.startsWith('/settings')) return 'settings'
  if (route.path.startsWith('/chat')) return 'chat'
  return ''
})

const displayName = computed(() => auth.user?.displayName ?? '我的账号')
</script>

<template>
  <nav class="ec-sidenav" aria-label="主导航">
    <RouterLink class="ec-sidenav__brand" to="/chat" aria-label="EiseChat 消息">
      <span class="ec-sidenav__logo" aria-hidden="true">E</span>
      <span class="ec-sidenav__brand-text">EiseChat</span>
    </RouterLink>

    <ul class="ec-sidenav__items">
      <li v-for="item in items" :key="item.key">
        <RouterLink
          v-if="item.to"
          :to="item.to"
          class="ec-sidenav__item"
          :class="{ 'ec-sidenav__item--active': activeKey === item.key }"
          :title="item.label"
          :aria-current="activeKey === item.key ? 'page' : undefined"
        >
          <AppIcon :name="item.icon" />
          <span class="ec-sidenav__label">{{ item.label }}</span>
        </RouterLink>
        <button
          v-else
          type="button"
          class="ec-sidenav__item ec-sidenav__item--soon"
          disabled
          :title="`${item.label}（即将上线）`"
          :aria-label="`${item.label}（即将上线）`"
        >
          <AppIcon :name="item.icon" />
          <span class="ec-sidenav__label">{{ item.label }}</span>
        </button>
      </li>
    </ul>

    <RouterLink class="ec-sidenav__account" to="/settings" title="账号与设置">
      <Avatar :display-name="displayName" :size="28" />
      <span class="ec-sidenav__account-name">{{ displayName }}</span>
    </RouterLink>
  </nav>
</template>

<style scoped>
.ec-sidenav {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  width: var(--ec-nav-width);
  padding: var(--ec-space-4) var(--ec-space-3);
  background: var(--ec-nav-bg);
  border-right: 1px solid var(--ec-border);
  overflow: hidden;
}

.ec-sidenav__brand {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  height: 40px;
  padding: 0 var(--ec-space-2);
  margin-bottom: var(--ec-space-2);
  color: var(--ec-text-primary);
  font-size: 16px;
  font-weight: var(--ec-font-weight-semibold);
  text-decoration: none;
}

.ec-sidenav__brand:hover {
  text-decoration: none;
}

.ec-sidenav__logo {
  display: grid;
  flex: none;
  width: 28px;
  height: 28px;
  place-items: center;
  border-radius: var(--ec-radius-sm);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 15px;
  font-weight: 700;
  line-height: 1;
}

.ec-sidenav__items {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  margin: 0;
  padding: 0;
  list-style: none;
}

.ec-sidenav__item {
  display: flex;
  gap: 10px;
  align-items: center;
  width: 100%;
  height: 40px;
  padding: 0 10px;
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-nav);
  text-align: left;
  text-decoration: none;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

.ec-sidenav__item:hover:not(:disabled) {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
  text-decoration: none;
}

.ec-sidenav__item--active,
.ec-sidenav__item--active:hover:not(:disabled) {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
}

.ec-sidenav__item--soon {
  color: var(--ec-disabled);
  cursor: not-allowed;
}

.ec-sidenav__label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-sidenav__account {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  min-width: 0;
  margin-top: auto;
  padding: var(--ec-space-3) 10px 0;
  border-top: 1px solid var(--ec-border);
  color: var(--ec-text-primary);
  font-size: 13px;
  text-decoration: none;
}

.ec-sidenav__account:hover {
  color: var(--ec-brand-600);
  text-decoration: none;
}

.ec-sidenav__account-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 801~1099：收为 72px 图标栏（Electron 最小窗口 960×640 下可用），文字隐藏、保留 title。 */
@media (max-width: 1099px) {
  .ec-sidenav {
    width: var(--ec-nav-width-compact);
    align-items: center;
    padding: var(--ec-space-4) var(--ec-space-2);
  }

  .ec-sidenav__brand {
    justify-content: center;
    padding: 0;
  }

  .ec-sidenav__brand-text,
  .ec-sidenav__label,
  .ec-sidenav__account-name {
    display: none;
  }

  .ec-sidenav__item,
  .ec-sidenav__account {
    justify-content: center;
  }

  .ec-sidenav__account {
    width: 100%;
    padding: var(--ec-space-3) 0 0;
  }
}

/* ≤800：单栏布局，导航隐藏（移动端底部导航见 UI-04）。 */
@media (max-width: 800px) {
  .ec-sidenav {
    display: none;
  }
}
</style>
