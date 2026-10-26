// 联系人资料卡（UI-03 批次2，计划 §5.8）：展示选中用户的昵称、在线状态、邮箱与「发送消息」入口。
// 纯展示组件：不直接调用接口，发起单聊由 Contacts 页处理（沿用原 createDirect 流程）。
<script setup lang="ts">
import { computed } from 'vue'
import type { UserProfile } from '../api/users'
import Avatar from './Avatar.vue'
import EmptyState from './EmptyState.vue'
import PresenceDot from './PresenceDot.vue'
import { presenceLabelOf, type PresenceState } from '../core/presence'

const props = defineProps<{
  user: UserProfile | null
  presence?: PresenceState
  /** 正在创建/打开会话（按钮禁用）。 */
  busy?: boolean
}>()

const emit = defineEmits<{ 'start-chat': [user: UserProfile] }>()

const presenceText = computed(() => presenceLabelOf(props.presence))
</script>

<template>
  <div class="contact-profile">
    <div v-if="user" class="contact-profile__card">
      <Avatar :display-name="user.displayName" :size="64" />
      <h3 class="contact-profile__name">{{ user.displayName }}</h3>
      <p class="contact-profile__presence">
        <PresenceDot :online="presence" />
        <span>{{ presenceText }}</span>
      </p>
      <p class="contact-profile__email">{{ user.email }}</p>
      <button
        type="button"
        class="contact-profile__action"
        :disabled="busy"
        @click="emit('start-chat', user)"
      >
        {{ busy ? '正在打开…' : '发送消息' }}
      </button>
    </div>
    <EmptyState
      v-else
      icon="user"
      title="搜索同事开始聊天"
      description="输入邮箱或昵称搜索，选中结果可查看资料并发送消息。"
    />
  </div>
</template>

<style scoped>
.contact-profile {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: var(--ec-space-2);
  height: 100%;
  padding: var(--ec-space-6);
  text-align: center;
}

.contact-profile__card {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  align-items: center;
  width: 100%;
  max-width: 420px;
}

.contact-profile__name {
  margin: var(--ec-space-2) 0 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-page-title);
  font-weight: var(--ec-font-weight-semibold);
}

.contact-profile__presence {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.contact-profile__email {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
}

.contact-profile__action {
  margin-top: var(--ec-space-3);
  padding: 8px var(--ec-space-5);
  border: none;
  border-radius: var(--ec-radius-md);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-body);
  cursor: pointer;
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
.contact-profile__action:hover:not(:disabled) {
  background: var(--ec-brand-700);
}
}

.contact-profile__action:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* UI-05 移动端：资料区改为卡片（白底/圆角/阴影），「发送消息」全宽 44px，触屏更易点。 */
@media (max-width: 800px) {
  .contact-profile {
    justify-content: flex-start;
    height: auto;
    min-height: 100%;
    padding: var(--ec-space-4);
    background: var(--ec-bg-canvas);
  }

  .contact-profile__card {
    margin-top: var(--ec-space-2);
    padding: var(--ec-space-6) var(--ec-space-4);
    border: 1px solid var(--ec-border);
    border-radius: var(--ec-radius-mobile-card);
    background: var(--ec-surface);
    box-shadow: var(--ec-shadow-sm);
  }

  .contact-profile__name {
    margin-top: 0;
    font-size: var(--ec-font-size-mobile-page-title);
  }

  .contact-profile__email {
    font-size: var(--ec-font-size-mobile-list-sub);
    word-break: break-all;
  }

  .contact-profile__action {
    align-self: stretch;
    min-height: var(--ec-touch-target);
    margin-top: var(--ec-space-4);
    border-radius: var(--ec-radius-mobile-input);
  }
}
</style>
