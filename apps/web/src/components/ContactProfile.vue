// 联系人资料卡（UI-03 批次2，F-17 批次5，C1b 拉黑）：展示昵称、在线状态、邮箱与操作入口。
// - 未选人：空态；陌生人：发送消息 / 加为好友；好友：发送消息 + 备注与分组（保存）+ 删除好友。
// - 任意联系人可拉黑/解除拉黑（拉黑后双方不能私聊、申请好友或邀请入群，服务端为准）。
// 纯展示组件：不直接调用接口，动作通过 emits 交给 Contacts 页处理。
<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import Avatar from './Avatar.vue'
import EmptyState from './EmptyState.vue'
import PresenceDot from './PresenceDot.vue'
import { presenceLabelOf, type PresenceState } from '../core/presence'
import type { Friend } from '../types/frames'

/** 资料区需要的最小用户信息（UserProfile 结构兼容；陌生人没有邮箱）。 */
interface ContactUser {
  userId: string
  displayName: string
  email?: string
  avatarFileId?: string | null
}

const props = defineProps<{
  user: ContactUser | null
  presence?: PresenceState
  /** 正在创建/打开会话（按钮禁用）。 */
  busy?: boolean
  /** 已是好友时传入好友关系（备注/分组/删除入口）。 */
  friend?: Friend | null
  /** 搜索结果中的关系：friend/outgoing/incoming，未搜索过为 null。 */
  relation?: 'friend' | 'outgoing' | 'incoming' | null
  /** 保存备注/分组中。 */
  saving?: boolean
  /** 删除好友中。 */
  removing?: boolean
  /** 当前用户已被我拉黑（按钮切换为「解除拉黑」）。 */
  blocked?: boolean
  /** 拉黑/解除拉黑请求进行中。 */
  blockBusy?: boolean
  /** 是否展示拉黑入口（Boolean prop 缺省会被 Vue 转成 false，需要显示时必须显式传 true）。 */
  showBlock?: boolean
}>()

const emit = defineEmits<{
  'start-chat': [user: ContactUser]
  'send-request': [user: ContactUser]
  'update-friend': [payload: { userId: string; remark?: string; groupName?: string }]
  'remove-friend': [friend: Friend]
  'toggle-block': [user: ContactUser]
  'preview-avatar': []
}>()

const presenceText = computed(() => presenceLabelOf(props.presence))
const displayName = computed(() => props.friend?.remark?.trim() || props.user?.displayName || '')

/** 成为好友的日期（本地化显示；无好友关系时不显示）。 */
const friendSinceText = computed(() => {
  const createdAt = props.friend?.createdAt
  if (!createdAt) return ''
  return new Date(createdAt).toLocaleDateString('zh-CN', {
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
  })
})

const remark = ref('')
const groupName = ref('')
const formError = ref('')

function syncForm(): void {
  remark.value = props.friend?.remark ?? ''
  groupName.value = props.friend?.groupName ?? ''
  formError.value = ''
}

watch(() => props.friend, syncForm, { immediate: true })
watch(() => props.user?.userId, syncForm)

const relationHint = computed(() => {
  switch (props.relation) {
    case 'outgoing':
      return '好友申请已发送，等待对方处理'
    case 'incoming':
      return '对方已向你发送好友申请，可在「申请」中处理'
    case 'friend':
      return ''
    default:
      return ''
  }
})

function saveFriend(): void {
  if (!props.user) return
  const nextRemark = remark.value.trim()
  const nextGroupName = groupName.value.trim()
  if (nextRemark.length > 64) {
    formError.value = '备注不能超过 64 个字符'
    return
  }
  if (nextGroupName.length > 32) {
    formError.value = '分组名不能超过 32 个字符'
    return
  }
  formError.value = ''
  // 空串表示清空备注/分组（服务端归一为 null），因此始终传字符串以便用户能清除旧值。
  emit('update-friend', {
    userId: props.user.userId,
    remark: nextRemark,
    groupName: nextGroupName,
  })
}
</script>

<template>
  <div class="contact-profile">
    <div v-if="user" class="contact-profile__card">
      <button
        type="button"
        class="contact-profile__avatar-button"
        aria-label="查看头像大图"
        title="查看头像大图"
        @click="emit('preview-avatar')"
      >
        <Avatar
          :display-name="displayName"
          :avatar-file-id="user.avatarFileId ?? undefined"
          :size="64"
        />
      </button>
      <h3 class="contact-profile__name">{{ displayName }}</h3>
      <p class="contact-profile__presence">
        <PresenceDot :online="presence" />
        <span>{{ presenceText }}</span>
      </p>
      <p v-if="user.email" class="contact-profile__email">
        <span class="contact-profile__email-label">邮箱</span>
        <span class="contact-profile__email-value">{{ user.email }}</span>
      </p>
      <p v-if="friendSinceText" class="contact-profile__meta">成为好友 · {{ friendSinceText }}</p>

      <div class="contact-profile__actions">
        <button
          type="button"
          class="contact-profile__action"
          :disabled="busy"
          @click="emit('start-chat', user)"
        >
          {{ busy ? '正在打开…' : '发送消息' }}
        </button>
        <button
          v-if="!friend"
          type="button"
          class="contact-profile__action contact-profile__action--secondary"
          :disabled="relation === 'outgoing'"
          @click="emit('send-request', user)"
        >
          {{ relation === 'outgoing' ? '已发送申请' : relation === 'incoming' ? '对方已申请' : '加为好友' }}
        </button>
      </div>

      <p v-if="relationHint" class="contact-profile__hint">{{ relationHint }}</p>

      <form v-if="friend" class="contact-profile__form" @submit.prevent="saveFriend">
        <h4 class="contact-profile__form-title">好友备注</h4>
        <label class="contact-profile__field">
          <span>备注名</span>
          <input
            v-model="remark"
            type="text"
            maxlength="64"
            placeholder="设置后优先显示备注"
            aria-label="好友备注名"
          />
        </label>
        <label class="contact-profile__field">
          <span>分组</span>
          <input
            v-model="groupName"
            type="text"
            maxlength="32"
            placeholder="例如：同事、项目组"
            aria-label="好友分组"
          />
        </label>
        <p v-if="formError" class="contact-profile__error" role="alert">{{ formError }}</p>
        <button type="submit" class="contact-profile__save" :disabled="saving">
          {{ saving ? '保存中…' : '保存备注' }}
        </button>
      </form>

      <button
        v-if="friend"
        type="button"
        class="contact-profile__remove"
        :disabled="removing"
        @click="emit('remove-friend', friend)"
      >
        {{ removing ? '正在删除…' : '删除好友' }}
      </button>

      <button
        v-if="user && showBlock !== false"
        type="button"
        class="contact-profile__remove contact-profile__remove--block"
        :disabled="blockBusy"
        @click="emit('toggle-block', user)"
      >
        {{ blockBusy ? '处理中…' : blocked ? '解除拉黑' : '拉黑' }}
      </button>
      <p v-if="blocked && showBlock !== false" class="contact-profile__hint">
        已拉黑：双方不能私聊、申请好友或邀请入群
      </p>
    </div>
    <EmptyState
      v-else
      icon="user"
      title="选择联系人查看资料"
      description="从好友或搜索结果中选择一位同事，可发送消息、加为好友或维护备注。"
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
  overflow-y: auto;
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
  word-break: break-word;
}

.contact-profile__presence {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.contact-profile__avatar-button {
  display: inline-flex;
  padding: 0;
  border: none;
  border-radius: var(--ec-radius-full, 999px);
  background: none;
  cursor: zoom-in;
}

.contact-profile__avatar-button:focus-visible {
  outline: 2px solid var(--ec-focus-ring);
  outline-offset: 3px;
}

.contact-profile__meta {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.contact-profile__email {
  display: flex;
  align-items: baseline;
  margin: 0;
  gap: var(--ec-space-2);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-body);
  word-break: break-all;
}

.contact-profile__email-label {
  flex: none;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.contact-profile__email-value {
  min-width: 0;
}

.contact-profile__actions {
  display: flex;
  flex-wrap: wrap;
  gap: var(--ec-space-2);
  justify-content: center;
  margin-top: var(--ec-space-3);
}

.contact-profile__action {
  padding: 8px var(--ec-space-5);
  border: none;
  border-radius: var(--ec-radius-md);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-body);
  cursor: pointer;
}

.contact-profile__action--secondary {
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
}

.contact-profile__action:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contact-profile__hint {
  margin: 0;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
}

.contact-profile__form {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
  width: 100%;
  margin-top: var(--ec-space-4);
  padding-top: var(--ec-space-4);
  border-top: 1px solid var(--ec-border-soft);
  text-align: left;
}

.contact-profile__form-title {
  margin: 0;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  font-weight: var(--ec-font-weight-semibold);
}

.contact-profile__field {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-1);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.contact-profile__field input {
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  background: var(--ec-surface);
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
}

.contact-profile__field input:focus-visible {
  border-color: var(--ec-brand-500);
  outline: none;
  box-shadow: var(--ec-focus-shadow);
}

.contact-profile__error {
  margin: 0;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-small);
}

.contact-profile__save {
  align-self: flex-start;
  padding: 6px var(--ec-space-4);
  border: 1px solid var(--ec-brand-600);
  border-radius: var(--ec-radius-md);
  background: var(--ec-surface);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
  font-weight: var(--ec-font-weight-medium);
  cursor: pointer;
}

.contact-profile__save:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.contact-profile__remove {
  margin-top: var(--ec-space-4);
  padding: 6px var(--ec-space-4);
  border: 1px solid var(--ec-danger);
  border-radius: var(--ec-radius-md);
  background: transparent;
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.contact-profile__remove:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

/* 拉黑是中性操作，与「删除好友」区分：不抢主色，也不做成危险色。 */
.contact-profile__remove--block {
  margin-top: var(--ec-space-2);
  border-color: var(--ec-border);
  color: var(--ec-text-secondary);
}

/* 悬停效果仅在鼠标精确指针设备生效，触屏不产生粘滞 hover。 */
@media (hover: hover) and (pointer: fine) {
  .contact-profile__action:hover:not(:disabled) {
    background: var(--ec-brand-700);
  }

  .contact-profile__action--secondary:hover:not(:disabled) {
    background: var(--ec-brand-100);
  }

  .contact-profile__save:hover:not(:disabled),
  .contact-profile__remove:hover:not(:disabled) {
    background: var(--ec-surface-hover);
  }
}

/* UI-05 移动端：资料区改为卡片（白底/圆角/阴影），按钮全宽 44px，触屏更易点。 */
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

  .contact-profile__actions {
    width: 100%;
  }

  .contact-profile__action {
    flex: 1;
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }

  .contact-profile__save,
  .contact-profile__remove {
    align-self: stretch;
    min-height: var(--ec-touch-target);
    border-radius: var(--ec-radius-mobile-input);
  }
}
</style>
