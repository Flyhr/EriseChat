<script setup lang="ts">
import { computed } from 'vue'
import { presenceLabelOf } from '../core/presence'
import AppIcon from './AppIcon.vue'
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
  }>(),
  { showBack: false },
)

const emit = defineEmits<{
  'open-group': []
  back: []
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

      <h2 class="ec-chat-header__title" :title="title">{{ title }}</h2>

      <template v-if="!isGroup">
        <PresenceDot :online="peerPresence" />
        <span class="ec-chat-header__presence">{{ presenceText }}</span>
      </template>
      <span v-else class="ec-chat-header__member-count">{{ memberCount }} 人</span>
    </div>

    <div class="ec-chat-header__actions">
      <ComingSoonHint label="搜索消息" layout="inline" :show-hint="false" class="ec-chat-header__action">
        <AppIcon name="search" />
      </ComingSoonHint>
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
  padding: 0 var(--ec-space-4);
  border-bottom: 1px solid var(--ec-border);
  background: var(--ec-surface);
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

.ec-chat-header__title {
  overflow: hidden;
  margin: 0;
  color: var(--ec-text-primary);
  font-size: 16px;
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
  padding: 2px var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface-hover);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-regular);
}

.ec-chat-header__actions {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  align-items: center;
}

.ec-chat-header__action {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 32px;
  height: 32px;
  padding: 0 var(--ec-space-1);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  cursor: not-allowed;
}

.ec-chat-header__action--text {
  padding: 0 var(--ec-space-2);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

.ec-chat-header__action--text:hover {
  background: var(--ec-brand-050);
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
</style>
