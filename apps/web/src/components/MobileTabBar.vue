<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import AppIcon from './AppIcon.vue'
import { type AppIconName } from '../core/icons'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'

// 移动端底部导航（UI-04，计划 §4.2 / §4.3 / §16.5）：
// 仅 ≤800 显示（CSS 控制，桌面不占布局）；进入会话详情时由 App.vue 隐藏。
// 批次 B1：一级入口与 SideNav 对齐为消息 / 联系人 / 收藏 / 设置；群聊是「消息」页内的筛选，
// 旧链接 /chat?group=all 仍有效且高亮「消息」；好友申请计数归联系人。
interface TabItem {
  key: string
  label: string
  icon: AppIconName
  to: string
}

const items: TabItem[] = [
  { key: 'chat', label: '消息', icon: 'chat', to: '/chat' },
  { key: 'contacts', label: '联系人', icon: 'contacts', to: '/contacts' },
  { key: 'favorites', label: '收藏', icon: 'star', to: '/favorites' },
  { key: 'settings', label: '设置', icon: 'settings', to: '/settings' },
]

const route = useRoute()
const conversations = useConversationsStore()
const friends = useFriendsStore()

/** 全部会话未读总和（服务端 unread + 本地已读优化）；消息页签角标。 */
const unreadTotal = computed(() => conversations.totalUnread)
/** F-17：待处理好友申请数，归「联系人」页签（与左导航同一口径）。 */
const pendingFriendCount = computed(() => friends.incomingCount)

/** 与 SideNav 相同的高亮规则：群聊筛选（?group=all）属于「消息」。 */
const activeKey = computed(() => {
  if (route.path.startsWith('/contacts')) return 'contacts'
  if (route.path.startsWith('/favorites')) return 'favorites'
  if (route.path.startsWith('/settings')) return 'settings'
  if (route.path.startsWith('/chat')) return 'chat'
  return ''
})

/** 页签角标文案：消息取未读总数，联系人取好友申请数，其余无角标。 */
function badgeOf(key: string): string {
  if (key === 'chat' && unreadTotal.value > 0) {
    return unreadTotal.value > 99 ? '99+' : String(unreadTotal.value)
  }
  if (key === 'contacts' && pendingFriendCount.value > 0) {
    return pendingFriendCount.value > 99 ? '99+' : String(pendingFriendCount.value)
  }
  return ''
}

/** 读屏提示：角标内容要能被读出，不能只靠视觉。 */
function ariaLabelOf(item: TabItem): string {
  if (item.key === 'chat' && unreadTotal.value > 0) return `${item.label}（${unreadTotal.value} 条未读）`
  if (item.key === 'contacts' && pendingFriendCount.value > 0) {
    return `${item.label}（${pendingFriendCount.value} 条好友申请）`
  }
  return item.label
}
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
      :aria-label="ariaLabelOf(item)"
    >
      <AppIcon :name="item.icon" :size="20" />
      <span v-if="badgeOf(item.key)" class="ec-tabbar__badge" aria-hidden="true">{{ badgeOf(item.key) }}</span>
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

  /* 未读角标：红色胶囊，位置对齐图标右上角；超过 99 显示 99+。 */
  .ec-tabbar__badge {
    position: absolute;
    top: 4px;
    left: calc(50% + 6px);
    min-width: 18px;
    height: 18px;
    padding: 0 5px;
    border: 2px solid var(--ec-surface);
    border-radius: var(--ec-radius-full);
    background: var(--ec-danger);
    color: var(--ec-text-inverse);
    font-size: 11px;
    font-weight: var(--ec-font-weight-semibold);
    line-height: 14px;
    text-align: center;
    box-sizing: border-box;
  }
}
</style>
