<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, ref, watch } from 'vue'
import { presenceLabelOf } from '../core/presence'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'
import PresenceDot from './PresenceDot.vue'

// 聊天顶栏（UI-02 批次2，计划 §5.3）：左侧标题 + 单聊在线状态/群成员数；
// 右侧搜索、共享文件与「更多」菜单（批次 B2：接本会话搜索、共享文件、免打扰、群资料）。
// 「更多」在手机上替代原来的不可用占位按钮，桌面保留搜索/文件直达图标。
const props = withDefaults(
  defineProps<{
    title: string
    isGroup: boolean
    memberCount: number
    /** 单聊对端在线状态（undefined=未知，不显示成离线）。 */
    peerPresence?: boolean
    /** 窄屏（≤800）显示返回按钮（批次4 启用）。 */
    showBack?: boolean
    /** 群头像 files.id（F-23）：仅群聊可能有值；null 表示未设置。 */
    avatarFileId?: string | null
    /** 当前会话是否免打扰：决定菜单里显示「消息免打扰」还是「取消免打扰」。 */
    muted?: boolean
    /** 是否显示 1:1 语音/视频入口（仅 Windows/Android 壳的单聊，Web 浏览器不声明通话能力）。 */
    callEnabled?: boolean
    /** 当前会话是否已满足通话门槛（好友或双方都发过消息）；不满足时按钮置灰但仍可点，用提示告知原因。 */
    callAllowed?: boolean
    /** 单聊对端账号原昵称：备注改名时头像首字母/配色不跟着变（群聊留空用标题）。 */
    avatarName?: string
  }>(),
  { showBack: false, avatarFileId: null, muted: false, callEnabled: false, callAllowed: true, avatarName: '' },
)

const emit = defineEmits<{
  'open-group': []
  'open-files': []
  'toggle-mute': []
  back: []
  search: []
  'call-audio': []
  'call-video': []
  'open-profile': []
}>()

const presenceText = computed(() => presenceLabelOf(props.peerPresence))

// 「更多」菜单：Esc 关闭并把焦点还给触发按钮；点击外部关闭。
const menuOpen = ref(false)
const moreButton = ref<HTMLButtonElement | null>(null)
const menuRef = ref<HTMLElement | null>(null)
const moreWrap = ref<HTMLElement | null>(null)

function closeMenu(returnFocus = true): void {
  if (!menuOpen.value) return
  menuOpen.value = false
  if (returnFocus) moreButton.value?.focus()
}

function toggleMenu(): void {
  if (menuOpen.value) {
    closeMenu(false)
    return
  }
  menuOpen.value = true
  void nextTick(() => menuRef.value?.querySelector<HTMLButtonElement>('[role="menuitem"]')?.focus())
}

function pick(action: 'search' | 'files' | 'mute' | 'group'): void {
  closeMenu(false)
  if (action === 'search') emit('search')
  else if (action === 'files') emit('open-files')
  else if (action === 'mute') emit('toggle-mute')
  else emit('open-group')
}

function onDocumentPointerDown(event: PointerEvent): void {
  if (!menuOpen.value) return
  const target = event.target as Node | null
  if (target && moreWrap.value?.contains(target)) return
  closeMenu(false)
}

function onDocumentKeyDown(event: KeyboardEvent): void {
  if (event.key !== 'Escape' || !menuOpen.value) return
  // 顶层浮层先消费 Esc：否则聊天页的 Esc 监听会同时把会话切回列表。
  event.preventDefault()
  closeMenu()
}

watch(menuOpen, (open) => {
  if (typeof document === 'undefined') return
  if (open) {
    document.addEventListener('pointerdown', onDocumentPointerDown, true)
    // 捕获阶段注册，保证早于聊天页的 bubble 监听。
    document.addEventListener('keydown', onDocumentKeyDown, true)
  } else {
    document.removeEventListener('pointerdown', onDocumentPointerDown, true)
    document.removeEventListener('keydown', onDocumentKeyDown, true)
  }
})

onBeforeUnmount(() => {
  if (typeof document === 'undefined') return
  document.removeEventListener('pointerdown', onDocumentPointerDown, true)
  document.removeEventListener('keydown', onDocumentKeyDown, true)
})

// 详情容器关闭后把焦点还给「更多」按钮（批次 B2 焦点归还）。
defineExpose({ moreButtonEl: moreButton })
</script>

<template>
  <header class="ec-chat-header">
    <div class="ec-chat-header__main">
      <button
        v-if="showBack"
        type="button"
        class="ec-chat-header__icon-button ec-chat-header__back"
        aria-label="返回会话列表"
        title="返回会话列表"
        @click="emit('back')"
      >
        <AppIcon name="back" />
      </button>

      <!-- UI-D P2：头像常驻（单聊用彩色首字母回退），在线状态改为标题下方副标题。
           单聊时头像与名称整体可点，进入对方资料页；群聊沿用右上角「群资料」。 -->
      <component
        :is="isGroup ? 'div' : 'button'"
        :class="['ec-chat-header__profile', { 'ec-chat-header__profile--static': isGroup }]"
        :type="isGroup ? undefined : 'button'"
        :aria-label="isGroup ? undefined : '查看对方资料'"
        :title="isGroup ? undefined : '查看对方资料'"
        @click="isGroup ? undefined : emit('open-profile')"
      >
        <Avatar
          :display-name="title"
          :seed="avatarName || title"
          :avatar-file-id="avatarFileId ?? undefined"
          :size="36"
          class="ec-chat-header__avatar"
        />

        <div class="ec-chat-header__identity">
          <h2 class="ec-chat-header__title" :title="title">{{ title }}</h2>
          <p class="ec-chat-header__subtitle">
            <template v-if="!isGroup">
              <PresenceDot :online="peerPresence" />
              <span>{{ presenceText }}</span>
            </template>
            <span v-else>{{ memberCount }} 人</span>
          </p>
        </div>
      </component>
    </div>

    <div class="ec-chat-header__actions">
      <!-- 1:1 通话入口（协议 §3.1）：只有 Windows/Android 壳的单聊显示。 -->
      <button
        v-if="callEnabled && !isGroup"
        type="button"
        :class="[
          'ec-chat-header__action',
          'ec-chat-header__icon-button',
          { 'ec-chat-header__action--disabled': !callAllowed },
        ]"
        :aria-disabled="!callAllowed"
        :title="callAllowed ? '语音通话' : '双方聊过之后才能发起通话'"
        aria-label="发起语音通话"
        @click="emit('call-audio')"
      >
        <AppIcon name="phone" />
      </button>
      <button
        v-if="callEnabled && !isGroup"
        type="button"
        :class="[
          'ec-chat-header__action',
          'ec-chat-header__icon-button',
          { 'ec-chat-header__action--disabled': !callAllowed },
        ]"
        :aria-disabled="!callAllowed"
        :title="callAllowed ? '视频通话' : '双方聊过之后才能发起通话'"
        aria-label="发起视频通话"
        @click="emit('call-video')"
      >
        <AppIcon name="video" />
      </button>
      <button
        type="button"
        class="ec-chat-header__action ec-chat-header__icon-button ec-chat-header__action--desktop"
        aria-label="搜索当前会话消息"
        title="搜索当前会话消息"
        @click="emit('search')"
      >
        <AppIcon name="search" />
      </button>
      <!-- F-15：会话共享文件入口，打开详情容器的「文件」页签。 -->
      <button
        type="button"
        class="ec-chat-header__action ec-chat-header__icon-button ec-chat-header__action--desktop"
        aria-label="查看会话共享文件"
        title="查看会话共享文件"
        @click="emit('open-files')"
      >
        <AppIcon name="file" />
      </button>

      <div ref="moreWrap" class="ec-chat-header__more-wrap">
        <button
          ref="moreButton"
          type="button"
          class="ec-chat-header__action ec-chat-header__icon-button"
          aria-label="更多"
          title="更多"
          aria-haspopup="menu"
          :aria-expanded="menuOpen"
          @click="toggleMenu"
        >
          <AppIcon name="more" />
        </button>
        <div
          v-if="menuOpen"
          ref="menuRef"
          class="ec-chat-header__menu"
          role="menu"
          aria-label="会话操作"
        >
          <button type="button" role="menuitem" class="ec-chat-header__menu-item" @click="pick('search')">
            <AppIcon name="search" :size="16" />
            <span>搜索本会话</span>
          </button>
          <button type="button" role="menuitem" class="ec-chat-header__menu-item" @click="pick('files')">
            <AppIcon name="file" :size="16" />
            <span>共享文件</span>
          </button>
          <button type="button" role="menuitem" class="ec-chat-header__menu-item" @click="pick('mute')">
            <AppIcon name="mute" :size="16" />
            <span>{{ muted ? '取消免打扰' : '消息免打扰' }}</span>
          </button>
          <button
            v-if="isGroup"
            type="button"
            role="menuitem"
            class="ec-chat-header__menu-item"
            @click="pick('group')"
          >
            <AppIcon name="groups" :size="16" />
            <span>群资料</span>
          </button>
        </div>
      </div>
    </div>
  </header>
</template>

<style scoped>
/* UI-D P2：标题 + 状态副标题两行结构；原有横向状态样式保留给窄屏降级。 */
.ec-chat-header__profile {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: var(--ec-space-2);
  padding: 0;
  border: none;
  background: none;
  color: inherit;
  font: inherit;
  text-align: left;
  cursor: pointer;
}

.ec-chat-header__profile--static {
  cursor: default;
}

.ec-chat-header__profile:focus-visible {
  outline: 2px solid var(--ec-focus-ring);
  outline-offset: 2px;
  border-radius: var(--ec-radius-panel);
}

.ec-chat-header__profile:hover .ec-chat-header__title {
  color: var(--ec-brand-600);
}

.ec-chat-header__identity {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.ec-chat-header__identity .ec-chat-header__title {
  margin: 0;
}

.ec-chat-header__subtitle {
  display: flex;
  gap: 4px;
  align-items: center;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: 1.3;
}

.ec-chat-header {
  display: flex;
  flex: none;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  height: var(--ec-header-height);
  padding: 0 var(--ec-space-5);
  border-bottom: 1px solid var(--ec-border-soft);
  /* 批次 B1：顶栏改实色，聊天区不再透出渐变/光晕。 */
  background: var(--ec-header-bg);
}

.ec-chat-header__main {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  min-width: 0;
}

.ec-chat-header__back {
  display: none;
}

/* F-23：群聊标题前的群头像。 */
.ec-chat-header__avatar {
  flex: none;
}

.ec-chat-header__title {
  overflow: hidden;
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 17px;
  font-weight: var(--ec-font-weight-semibold);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ec-chat-header__actions {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  align-items: center;
}

.ec-chat-header__action {
  display: inline-flex;
  flex: none;
  align-items: center;
  justify-content: center;
  min-width: 32px;
  height: 32px;
  padding: 0 var(--ec-space-1);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  white-space: nowrap;
  cursor: pointer;
  transition: background var(--ec-transition-fast) var(--ec-ease);
}

@media (hover: hover) and (pointer: fine) {
  .ec-chat-header__action:hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }
}

.ec-chat-header__icon-button {
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
}

.ec-chat-header__action:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

/* 未满足通话门槛：置灰但仍可点，客户端用 toast 说明原因（隐藏入口会让人找不到通话）。 */
.ec-chat-header__action--disabled {
  cursor: not-allowed;
  opacity: 0.38;
}

/* 「更多」菜单：相对触发按钮定位；阴影只用于浮层（批次 B1 规则）。 */
.ec-chat-header__more-wrap {
  position: relative;
  display: inline-flex;
}

.ec-chat-header__menu {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  z-index: 30;
  display: flex;
  flex-direction: column;
  min-width: 168px;
  padding: var(--ec-space-1);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-lg);
}

.ec-chat-header__menu-item {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  min-height: 36px;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-secondary);
  text-align: left;
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
  .ec-chat-header__menu-item:hover {
    background: var(--ec-surface-hover);
  }
}

.ec-chat-header__menu-item:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: 1px;
}

/* ≤800：移动单栏聊天详情，显示返回按钮。 */
@media (max-width: 800px) {
  .ec-chat-header__back {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 32px;
    height: 32px;
    border-radius: var(--ec-radius-sm);
    color: var(--ec-text-secondary);
  }
}

/* UI-04 批次4：移动端顶栏按钮触控热区 ≥44×44。 */
@media (max-width: 800px) {
  .ec-chat-header__icon-button,
  .ec-chat-header__action {
    min-width: 44px;
    min-height: 44px;
  }

  .ec-chat-header__menu-item {
    min-height: 44px;
  }
}

/* UI-05 移动端：顶栏压到 60px、标题 17px；搜索/文件直达图标收进「更多」菜单，避免顶栏拥挤。 */
@media (max-width: 800px) {
  .ec-chat-header {
    gap: var(--ec-space-2);
    height: var(--ec-header-height-mobile);
    padding: 0 var(--ec-space-2) 0 var(--ec-space-1);
  }

  .ec-chat-header__title {
    font-size: var(--ec-font-size-mobile-header);
  }

  .ec-chat-header__action--desktop {
    display: none;
  }
}
</style>
