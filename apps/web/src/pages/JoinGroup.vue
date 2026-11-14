<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { inviteLinksApi, type GroupInvitePreview } from '../api/inviteLinks'
import { friendlyError } from '../core/errorMessages'
import { isInviteTokenLike } from '../core/inviteToken'
import { showToast } from '../core/toast'
import { useConversationsStore } from '../stores/conversations'

// 群邀请链接落地页（C1c）：登录后由用户确认再加入；服务端负责有效期、撤销、发起人权限、
// 拉黑与群容量校验，页面只展示预览与结果。令牌不写入日志、不展示明文。
const route = useRoute()
const router = useRouter()
const conversations = useConversationsStore()

const token = typeof route.params.token === 'string' ? route.params.token : ''
const loading = ref(true)
const joining = ref(false)
const preview = ref<GroupInvitePreview | null>(null)
const errorMessage = ref('')

onMounted(loadPreview)

async function loadPreview(): Promise<void> {
  if (!isInviteTokenLike(token)) {
    errorMessage.value = '邀请链接无效'
    loading.value = false
    return
  }
  try {
    preview.value = await inviteLinksApi.preview(token)
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    loading.value = false
  }
}

async function join(): Promise<void> {
  if (joining.value) return
  joining.value = true
  try {
    const conversation = await inviteLinksApi.join(token)
    conversations.upsert([conversation])
    showToast('已加入群聊', 'success')
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    showToast(friendlyError(error), 'error')
  } finally {
    joining.value = false
  }
}

function openConversation(): void {
  if (!preview.value) return
  void router.push({ name: 'chat', params: { conversationId: preview.value.conversationId } })
}

function goHome(): void {
  void router.push({ name: 'chat' })
}
</script>

<template>
  <main class="join-page">
    <section class="join-card" :aria-busy="loading || joining">
      <p v-if="loading" class="join-card__hint">正在打开邀请链接…</p>

      <template v-else-if="errorMessage">
        <h1 class="join-card__title">邀请链接不可用</h1>
        <p class="join-card__hint">{{ errorMessage }}</p>
        <button class="join-card__action" type="button" @click="goHome">回到消息</button>
      </template>

      <template v-else-if="preview">
        <h1 class="join-card__title">{{ preview.title }}</h1>
        <p class="join-card__meta">
          群主 {{ preview.creatorDisplayName }} · {{ preview.memberCount }}/{{ preview.maxMembers }} 人
        </p>

        <p v-if="preview.revoked" class="join-card__hint">链接已被撤销</p>
        <p v-else-if="preview.expired" class="join-card__hint">链接已过期</p>
        <p v-else-if="preview.alreadyMember" class="join-card__hint">你已在该群聊中</p>

        <button v-if="preview.alreadyMember" class="join-card__action" type="button" @click="openConversation">
          进入群聊
        </button>
        <button
          v-else-if="!preview.revoked && !preview.expired"
          class="join-card__action"
          type="button"
          :disabled="joining"
          @click="join"
        >
          {{ joining ? '加入中…' : '加入群聊' }}
        </button>
        <button
          v-if="preview.revoked || preview.expired"
          class="join-card__action join-card__action--plain"
          type="button"
          @click="goHome"
        >
          回到消息
        </button>
      </template>
    </section>
  </main>
</template>

<style scoped>
.join-page {
  display: flex;
  min-height: 100vh;
  min-height: 100dvh;
  align-items: center;
  justify-content: center;
  padding: var(--ec-space-6);
  background: var(--ec-bg-canvas);
}

.join-card {
  display: flex;
  width: min(420px, 100%);
  flex-direction: column;
  gap: var(--ec-space-3);
  padding: var(--ec-space-6);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-lg);
  background: var(--ec-surface);
  box-shadow: var(--ec-shadow-sm);
}

.join-card__title {
  margin: 0;
  font-size: var(--ec-font-size-page-title);
  font-weight: 600;
  color: var(--ec-text-primary);
}

.join-card__meta,
.join-card__hint {
  margin: 0;
  font-size: var(--ec-font-size-secondary);
  color: var(--ec-text-secondary);
}

.join-card__action {
  align-self: flex-start;
  padding: var(--ec-space-2) var(--ec-space-4);
  border: none;
  border-radius: var(--ec-radius-md);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
  transition: opacity var(--ec-transition-fast) var(--ec-ease);
}

.join-card__action:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.join-card__action--plain {
  background: transparent;
  color: var(--ec-text-secondary);
  border: 1px solid var(--ec-border);
}
</style>
