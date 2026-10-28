<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import AppIcon from './AppIcon.vue'
import { type AppIconName } from '../core/icons'

// 移动端底部导航（UI-04，计划 §4.2 / §4.3 / §16.5）：
// 仅 ≤800 显示（CSS 控制，桌面不占布局）；进入会话详情时由 App.vue 隐藏。
// 一级入口与 SideNav 保持一致，高亮规则复用同一判断（群聊筛选 ?group=all）。
interface TabItem {
  key: string
  label: string
  icon: AppIconName
  to: string
}

const items: TabItem[] = [
  { key: 'chat', label: '消息', icon: 'chat', to: '/chat' },
  { key: 'contacts', label: '联系人', icon: 'contacts', to: '/contacts' },
  { key: 'groups', label: '群聊', icon: 'groups', to: '/chat?group=all' },
  { key: 'favorites', label: '收藏', icon: 'star', to: '/favorites' },
  { key: 'settings', label: '设置', icon: 'settings', to: '/settings' },
]

const route = useRoute()

/** 与 SideNav 相同的高亮规则：群聊筛选优先于消息。 */
const activeKey = computed(() => {
  if (route.path.startsWith('/contacts')) return 'contacts'
  if (route.path.startsWith('/favorites')) return 'favorites'
  if (route.path.startsWith('/settings')) return 'settings'
  if (route.path.startsWith('/chat')) {
    return route.query.group === 'all' ? 'groups' : 'chat'
  }
  return ''
})
</script>

<template>
  <nav class="ec-tabbar" aria-label="移动端主导航">
    <RouterLink
      v-for="item in items"
      :key="item.key"
      :to="item.to"
      class="ec-tabbar__item"
      :class="{ 'ec-tabbar__item--active': activeKey === item.key }"
      :aria-current="activeKey === item.key ? 'page' : undefined"
    >
      <AppIcon :name="item.icon" :size="20" />
      <span class="ec-tabbar__label">{{ item.label }}</span>
    </RouterLink>
  </nav>
</template>

<style scoped>
/* 默认不渲染（display:none 不参与网格布局）；≤800 才显示为底部导航。 */
.ec-tabbar {
  display: none;
}

@media (max-width: 800px) {
  .ec-tabbar {
    display: flex;
    flex: none;
    align-items: stretch;
    height: calc(58px + env(safe-area-inset-bottom, 0px));
    padding-bottom: env(safe-area-inset-bottom, 0px);
    border-top: 1px solid var(--ec-border);
    background: var(--ec-surface);
    box-shadow: 0 -1px 0 var(--ec-border);
  }

  .ec-tabbar__item {
    position: relative;
    display: flex;
    flex: 1;
    flex-direction: column;
    gap: 3px;
    align-items: center;
    justify-content: center;
    min-width: var(--ec-touch-target);
    min-height: var(--ec-touch-target);
    color: var(--ec-text-secondary);
    font-size: 11px;
    text-decoration: none;
    -webkit-tap-highlight-color: transparent;
  }

  /* 选中项：品牌色 + 顶部 2px 指示条，触屏无需悬停即可辨认当前页。 */
  .ec-tabbar__item--active {
    color: var(--ec-brand-600);
  }

  .ec-tabbar__item--active::before {
    position: absolute;
    top: 0;
    left: 50%;
    width: 20px;
    height: 2px;
    border-radius: var(--ec-radius-full);
    background: var(--ec-brand-600);
    content: '';
    transform: translateX(-50%);
  }

  .ec-tabbar__item--active .ec-tabbar__label {
    font-weight: var(--ec-font-weight-semibold);
  }

  .ec-tabbar__item :deep(svg) {
    width: 22px;
    height: 22px;
  }

  .ec-tabbar__item:focus-visible {
    outline: var(--ec-focus-width) solid var(--ec-focus-ring);
    outline-offset: calc(var(--ec-focus-offset) * -1);
    border-radius: var(--ec-radius-sm);
  }

  .ec-tabbar__label {
    line-height: 14px;
  }
}
</style>
