<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { RouterLink, useRoute } from 'vue-router'
import AppIcon from './AppIcon.vue'
import { type AppIconName } from '../core/icons'
import Avatar from './Avatar.vue'
import { chatWs, type WsState } from '../core/ws'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'
import { useFriendsStore } from '../stores/friends'

// 一级导航（UI-01，计划 §2.1 / §5.1 / §16.5）：
// ≥1200：172px 图标+文字；801~1199：72px 图标栏（hover/focus 有 title 提示）；≤800 隐藏（移动端导航 UI-04）。
// 群聊入口（UI-02）：/chat?group=all 进入会话列表的「群聊」筛选；高亮时群聊优先于消息。
// UI-D P2：补回参考图丢失的结构——品牌副信息、工作空间分组、独立设置区、连接状态、账号双行卡片。
interface NavItem {
  key: string
  label: string
  icon: AppIconName
  to: string
}

const mainItems: NavItem[] = [
  { key: 'chat', label: '消息', icon: 'chat', to: '/chat' },
  { key: 'contacts', label: '联系人', icon: 'contacts', to: '/contacts' },
  { key: 'groups', label: '群聊', icon: 'groups', to: '/chat?group=all' },
  { key: 'favorites', label: '收藏', icon: 'star', to: '/favorites' },
]
const settingsItem: NavItem = { key: 'settings', label: '设置', icon: 'settings', to: '/settings' }

const route = useRoute()
const auth = useAuthStore()
const conversations = useConversationsStore()
const friends = useFriendsStore()
// F-02：全局未读数（左导航「消息」徽标；桌面壳另由 App.vue 同步到托盘/任务栏）。
const totalUnread = computed(() => conversations.totalUnread)
const unreadBadgeText = computed(() => (totalUnread.value > 99 ? '99+' : String(totalUnread.value)))

// F-17：待处理好友申请数（左导航「联系人」徽标）。
const pendingFriendCount = computed(() => friends.incomingCount)

function badgeTextOf(key: string): string {
  if (key === 'chat' && totalUnread.value > 0) return unreadBadgeText.value
  if (key === 'contacts' && pendingFriendCount.value > 0) {
    return pendingFriendCount.value > 99 ? '99+' : String(pendingFriendCount.value)
  }
  return ''
}

function ariaLabelOf(item: NavItem): string {
  if (item.key === 'chat' && totalUnread.value > 0) {
    return `${item.label}（${totalUnread.value} 条未读）`
  }
  if (item.key === 'contacts' && pendingFriendCount.value > 0) {
    return `${item.label}（${pendingFriendCount.value} 条好友申请）`
  }
  return item.label
}

/** 当前一级导航高亮：按路由前缀 + 聊天页群聊筛选（?group=all）判断。 */
const activeKey = computed(() => {
  if (route.path.startsWith('/contacts')) return 'contacts'
  if (route.path.startsWith('/favorites')) return 'favorites'
  if (route.path.startsWith('/settings')) return 'settings'
  if (route.path.startsWith('/chat')) {
    return route.query.group === 'all' ? 'groups' : 'chat'
  }
  return ''
})

const displayName = computed(() => auth.user?.displayName ?? '我的账号')
const brandSubtitle = computed(() => auth.user?.email ?? 'EriseChat')
const accountSubtitle = computed(() => '账号与设置')

// 连接状态：只订阅 WS 状态变化，不改动连接逻辑（App.vue 仍是唯一连接横幅来源）。
const connectionState = ref<WsState>(chatWs.state)
let unsubscribeState: (() => void) | null = null
onMounted(() => {
  unsubscribeState = chatWs.on('statechange', (state) => {
    connectionState.value = state
  })
})
onBeforeUnmount(() => unsubscribeState?.())
const connected = computed(() => connectionState.value === 'authed')
const connectionLabel = computed(() => (connected.value ? '服务器已连接' : '服务器未连接'))
</script>

<template>
  <nav class="ec-sidenav" aria-label="主导航">
    <RouterLink class="ec-sidenav__brand" to="/chat" aria-label="EriseChat 消息">
      <span class="ec-sidenav__logo" aria-hidden="true">E</span>
      <span class="ec-sidenav__brand-text">
        <span class="ec-sidenav__brand-name">EriseChat</span>
        <span class="ec-sidenav__brand-sub">{{ brandSubtitle }}</span>
      </span>
    </RouterLink>

    <p class="ec-sidenav__group">工作空间</p>
    <ul class="ec-sidenav__items">
      <li v-for="item in mainItems" :key="item.key">
        <RouterLink
          :to="item.to"
          class="ec-sidenav__item"
          :class="{ 'ec-sidenav__item--active': activeKey === item.key }"
          :title="item.label"
          :aria-current="activeKey === item.key ? 'page' : undefined"
          :aria-label="ariaLabelOf(item)"
        >
          <AppIcon :name="item.icon" />
          <span class="ec-sidenav__label">{{ item.label }}</span>
          <span
            v-if="badgeTextOf(item.key)"
            class="ec-sidenav__badge"
            aria-hidden="true"
          >{{ badgeTextOf(item.key) }}</span>
        </RouterLink>
      </li>
    </ul>

    <div class="ec-sidenav__bottom">
      <RouterLink
        :to="settingsItem.to"
        class="ec-sidenav__item"
        :class="{ 'ec-sidenav__item--active': activeKey === settingsItem.key }"
        :title="settingsItem.label"
        :aria-current="activeKey === settingsItem.key ? 'page' : undefined"
        :aria-label="settingsItem.label"
      >
        <AppIcon :name="settingsItem.icon" />
        <span class="ec-sidenav__label">{{ settingsItem.label }}</span>
      </RouterLink>

      <p class="ec-sidenav__connection" :class="{ 'ec-sidenav__connection--offline': !connected }">
        <span class="ec-sidenav__connection-dot" aria-hidden="true" />
        <span class="ec-sidenav__connection-label">{{ connectionLabel }}</span>
      </p>

      <RouterLink class="ec-sidenav__account" to="/settings" title="账号与设置">
        <Avatar
          :display-name="displayName"
          :avatar-file-id="auth.user?.avatarFileId"
          :size="28"
        />
        <span class="ec-sidenav__account-text">
          <span class="ec-sidenav__account-name">{{ displayName }}</span>
          <span class="ec-sidenav__account-sub">{{ accountSubtitle }}</span>
        </span>
        <AppIcon name="more" :size="16" class="ec-sidenav__account-more" />
      </RouterLink>
    </div>
  </nav>
</template>

<style scoped>
/* UI-D P2：参考图结构补齐。新增规则放在最前，避免覆盖文件末尾的响应式 media 覆盖。 */
.ec-sidenav__brand-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.ec-sidenav__brand-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-sidenav__brand-sub {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: 11px;
  font-weight: var(--ec-font-weight-regular);
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-sidenav__group {
  margin: var(--ec-space-2) 0 0;
  padding: 0 var(--ec-space-2);
  color: var(--ec-text-tertiary);
  font-size: 11px;
  letter-spacing: 0.04em;
}

.ec-sidenav__bottom {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  margin-top: auto;
}

.ec-sidenav__connection {
  display: flex;
  gap: 6px;
  align-items: center;
  margin: 0;
  padding: 0 var(--ec-space-2);
  color: var(--ec-text-secondary);
  font-size: 11px;
}

.ec-sidenav__connection-dot {
  flex: none;
  width: 6px;
  height: 6px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-online);
}

.ec-sidenav__connection--offline .ec-sidenav__connection-dot {
  background: var(--ec-disabled);
}

.ec-sidenav__account-text {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.ec-sidenav__account-sub {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: 11px;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-sidenav__account-more {
  flex: none;
  margin-left: auto;
  color: var(--ec-text-tertiary);
}

/* 紧凑图标栏（801~1199）：隐藏新增的文字细节，只保留图标与状态点。 */
@media (max-width: 1199px) {
  .ec-sidenav__brand-sub,
  .ec-sidenav__group,
  .ec-sidenav__account-sub,
  .ec-sidenav__account-more,
  .ec-sidenav__connection-label {
    display: none;
  }
}

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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-sidenav__brand:hover {
  text-decoration: none;
}
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
  position: relative;
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-sidenav__item:hover:not(:disabled) {
  background: var(--ec-surface-hover);
  color: var(--ec-text-primary);
  text-decoration: none;
}
}

/* 选中态与 hover 无关，触屏/窄栏同样需要可见（不能放进 hover 媒体查询）。 */
.ec-sidenav__item--active,
.ec-sidenav__item--active:hover:not(:disabled) {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-weight: var(--ec-font-weight-semibold);
}

/* UI-D P2：选中项左侧品牌色竖条（参考图 01/02/03）。 */
.ec-sidenav__item--active::before {
  position: absolute;
  top: 8px;
  bottom: 8px;
  left: 0;
  width: 3px;
  border-radius: 0 var(--ec-radius-full) var(--ec-radius-full) 0;
  background: var(--ec-brand-600);
  content: '';
}

/* F-02：全局未读徽标（消息入口）；紧凑图标栏时贴到图标右上角。 */
.ec-sidenav__badge {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 18px;
  margin-left: auto;
  padding: 0 5px;
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: 11px;
  font-weight: var(--ec-font-weight-semibold);
  line-height: 1;
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

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-sidenav__account:hover {
  color: var(--ec-brand-600);
  text-decoration: none;
}
}

.ec-sidenav__account-name {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 801~1199：收为 72px 图标栏（Electron 最小窗口 960×640 下可用），文字隐藏、保留 title。 */
@media (max-width: 1199px) {
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

  /* 紧凑图标栏：徽标收成图标右上角小圆点，不撑开 72px 栏宽。 */
  .ec-sidenav__badge {
    position: absolute;
    top: 5px;
    right: 7px;
    min-width: 16px;
    height: 16px;
    margin-left: 0;
    padding: 0 4px;
    font-size: 10px;
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
