<script setup lang="ts">
import { computed } from 'vue'
import { presenceLabelOf } from '../core/presence'
import AppIcon from './AppIcon.vue'
import Avatar from './Avatar.vue'
import ComingSoonHint from './ComingSoonHint.vue'
import PresenceDot from './PresenceDot.vue'

// 聊天顶栏（UI-02 批次2，计划 §5.3）：左侧标题 + 单聊在线状态/群成员数；
// 右侧资料入口与占位按钮（消息搜索未实现时灰显，不放假按钮）；返回按钮供 ≤800 移动布局（批次4）启用。
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
  }>(),
  { showBack: false, avatarFileId: null },
)

const emit = defineEmits<{
  'open-group': []
  'open-files': []
  back: []
  search: []
}>()

const presenceText = computed(() => presenceLabelOf(props.peerPresence))
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

      <Avatar
        v-if="isGroup && avatarFileId"
        :display-name="title"
        :avatar-file-id="avatarFileId"
        :size="32"
        class="ec-chat-header__avatar"
      />

      <h2 class="ec-chat-header__title" :title="title">{{ title }}</h2>

      <template v-if="!isGroup">
        <PresenceDot :online="peerPresence" />
        <span class="ec-chat-header__presence">{{ presenceText }}</span>
      </template>
      <span v-else class="ec-chat-header__member-count">{{ memberCount }} 人</span>
    </div>

    <div class="ec-chat-header__actions">
      <button
        type="button"
        class="ec-chat-header__action ec-chat-header__icon-button"
        aria-label="搜索当前会话消息"
        title="搜索当前会话消息"
        @click="emit('search')"
      >
        <AppIcon name="search" />
      </button>
      <!-- F-15：会话共享文件入口（群聊/单聊都可用），打开右侧 aside 的「文件」页签。 -->
      <button
        type="button"
        class="ec-chat-header__action ec-chat-header__icon-button"
        aria-label="查看会话共享文件"
        title="查看会话共享文件"
        @click="emit('open-files')"
      >
        <AppIcon name="file" />
      </button>
      <ComingSoonHint label="更多" layout="inline" :show-hint="false" class="ec-chat-header__action">
        <AppIcon name="more" />
      </ComingSoonHint>
      <button
        v-if="isGroup"
        type="button"
        class="ec-chat-header__action ec-chat-header__action--text"
        @click="emit('open-group')"
      >
        群资料
      </button>
    </div>
  </header>
</template>

<style scoped>
.ec-chat-header {
  display: flex;
  flex: none;
  gap: var(--ec-space-3);
  align-items: center;
  justify-content: space-between;
  height: var(--ec-header-height);
  padding: 0 var(--ec-space-5);
  border-bottom: 1px solid var(--ec-border-soft);
  /* 「澄蓝」主题：半透明磨砂顶栏，聊天区光晕透出。 */
  background: var(--ec-header-bg);
  -webkit-backdrop-filter: var(--ec-header-blur);
  backdrop-filter: var(--ec-header-blur);
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

.ec-chat-header__presence {
  flex: none;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-regular);
}

.ec-chat-header__member-count {
  flex: none;
  padding: 3px var(--ec-space-2);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: rgba(255, 255, 255, 0.72);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-regular);
}

.ec-chat-header__actions {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  align-items: center;
}

/* ComingSoonHint 的 inline 布局默认撑满整行（用于设置页整行占位），
   在顶栏这里要作为 32px 图标按钮使用，必须收掉 width:100%。 */
.ec-chat-header__actions :deep(.ec-coming-soon--inline) {
  width: auto;
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
  cursor: not-allowed;
}

.ec-chat-header__action--text {
  padding: 0 var(--ec-space-2);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.ec-chat-header__action--text:hover {
  background: var(--ec-brand-050);
}
}

.ec-chat-header__icon-button {
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
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
  .ec-chat-header__action,
  .ec-chat-header__action--text {
    min-width: 44px;
    min-height: 44px;
  }
}

/* UI-05 移动端：顶栏压到 60px、标题 17px；「搜索/更多」占位入口在窄屏隐藏，
   保留真实的「群资料」按钮，避免顶栏拥挤。 */
@media (max-width: 800px) {
  .ec-chat-header {
    gap: var(--ec-space-2);
    height: var(--ec-header-height-mobile);
    padding: 0 var(--ec-space-2) 0 var(--ec-space-1);
  }

  .ec-chat-header__title {
    font-size: var(--ec-font-size-mobile-header);
  }

  .ec-chat-header__actions > .ec-chat-header__action:not(.ec-chat-header__action--text) {
    display: none;
  }

  .ec-chat-header__action--text {
    height: var(--ec-touch-target);
  }
}
</style>
