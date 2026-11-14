<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { inviteLinksApi, type GroupInvitePreview } from '../api/inviteLinks'
import { resolveServerOrigin } from '../core/config'
import { friendlyError } from '../core/errorMessages'
import { isInviteTokenLike } from '../core/inviteToken'
import { showToast } from '../core/toast'
import { useAuthStore } from '../stores/auth'
import { useConversationsStore } from '../stores/conversations'

// 群邀请链接落地页（C1c）：登录后由用户确认再加入；服务端负责有效期、撤销、发起人权限、
// 拉黑与群容量校验，页面只展示预览与结果。令牌不写入日志、不展示明文。
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const conversations = useConversationsStore()

// 令牌跟随当前路由：同一应用内从 /join/A 切到 /join/B 会复用本组件，不能只在挂载时读一次。
const token = computed(() => (typeof route.params.token === 'string' ? route.params.token : ''))
const loading = ref(true)
const joining = ref(false)
const preview = ref<GroupInvitePreview | null>(null)
const errorMessage = ref('')
// 预览请求序号：切换链接后旧请求返回时直接丢弃，不能覆盖新链接的加载态与预览。
let previewRequest = 0

watch(token, () => void loadPreview(), { immediate: true })

async function loadPreview(): Promise<void> {
  const currentToken = token.value
  const request = ++previewRequest
  preview.value = null
  errorMessage.value = ''
  loading.value = true
  if (!isInviteTokenLike(currentToken)) {
    errorMessage.value = '邀请链接无效'
    loading.value = false
    return
  }
  try {
    const result = await inviteLinksApi.preview(currentToken)
    if (request !== previewRequest) return
    preview.value = result
  } catch (error) {
    if (request !== previewRequest) return
    errorMessage.value = friendlyError(error)
  } finally {
    if (request === previewRequest) loading.value = false
  }
}

/** 兑换发起时的登录身份；响应回来时用于判断期间是否退出登录、被踢、切换账号或服务器。 */
interface JoinSession {
  token: string | null
  userId: string
  origin: string
}

function captureSession(): JoinSession {
  return { token: auth.accessToken, userId: auth.user?.userId ?? '', origin: resolveServerOrigin() }
}

function isSameSession(session: JoinSession): boolean {
  return (
    auth.accessToken === session.token &&
    (auth.user?.userId ?? '') === session.userId &&
    resolveServerOrigin() === session.origin
  )
}

async function join(): Promise<void> {
  if (joining.value) return
  // 兑换绑定点击时的令牌：地址已切到别的链接时不得沿用旧预览的令牌。
  const currentToken = token.value
  if (!isInviteTokenLike(currentToken)) return
  const session = captureSession()
  if (!session.token) return
  joining.value = true
  try {
    const conversation = await inviteLinksApi.join(currentToken)
    // 迟到的成功响应：期间已退出登录/被踢/切换账号或服务器，或地址已切到别的链接时，
    // 不得把旧会话写回缓存（upsert 会写 IndexedDB），也不再提示与跳转。
    if (!isSameSession(session) || currentToken !== token.value) return
    conversations.upsert([conversation])
    showToast('已加入群聊', 'success')
    await router.push({ name: 'chat', params: { conversationId: conversation.conversationId } })
  } catch (error) {
    if (!isSameSession(session) || currentToken !== token.value) return
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
