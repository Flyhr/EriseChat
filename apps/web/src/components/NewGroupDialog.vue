// 新建群聊弹窗（UI-03 批次2，计划 §5.9）：基于 AppDialog 的自绘表单，
// 群名称 + 搜索并多选成员 → 调用原 conversations.createGroup（校验走 core/groupForm.ts）。
// 服务端 VALIDATION_FAILED 的具体文案原样展示，不自行编造。
<script setup lang="ts">
import { watch, ref } from 'vue'
import { usersApi, type UserProfile } from '../api/users'
import type { Conversation } from '../types/frames'
import { friendlyError } from '../core/errorMessages'
import { GROUP_TITLE_MAX_LENGTH, validateGroupForm } from '../core/groupForm'
import { useConversationsStore } from '../stores/conversations'
import AppDialog from './AppDialog.vue'
import Avatar from './Avatar.vue'

const props = defineProps<{ open: boolean }>()
const emit = defineEmits<{ close: []; created: [conversation: Conversation] }>()

const conversations = useConversationsStore()

const title = ref('')
const keyword = ref('')
const results = ref<UserProfile[]>([])
const selectedMembers = ref<UserProfile[]>([])
const searching = ref(false)
const searched = ref(false)
const submitting = ref(false)
const errorMessage = ref('')

function isSelected(userId: string): boolean {
  return selectedMembers.value.some((member) => member.userId === userId)
}

function toggleMember(user: UserProfile): void {
  selectedMembers.value = isSelected(user.userId)
    ? selectedMembers.value.filter((member) => member.userId !== user.userId)
    : [...selectedMembers.value, user]
}

function removeSelected(userId: string): void {
  selectedMembers.value = selectedMembers.value.filter((member) => member.userId !== userId)
}

async function search(): Promise<void> {
  const trimmed = keyword.value.trim()
  if (trimmed.length === 0) {
    errorMessage.value = '请输入昵称或完整邮箱'
    return
  }
  searching.value = true
  errorMessage.value = ''
  try {
    const response = await usersApi.search(trimmed)
    results.value = response.users
    searched.value = true
  } catch (error) {
    errorMessage.value = friendlyError(error)
  } finally {
    searching.value = false
  }
}

async function submit(): Promise<void> {
  if (submitting.value) return
  const validationError = validateGroupForm(
    title.value,
    selectedMembers.value.map((member) => member.userId),
  )
  if (validationError) {
    errorMessage.value = validationError
    return
  }
  submitting.value = true
  errorMessage.value = ''
  try {
    const conversation = await conversations.createGroup(
      title.value.trim(),
      selectedMembers.value.map((member) => member.userId),
    )
    emit('created', conversation)
    emit('close')
    reset()
  } catch (error) {
    // 服务端 VALIDATION_FAILED 的具体文案（人数上限/成员不存在等）原样展示。
    errorMessage.value = friendlyError(error)
  } finally {
    submitting.value = false
  }
}

function close(): void {
  emit('close')
}

/** 每次重新打开时清空上次输入，避免串数据。 */
function reset(): void {
  title.value = ''
  keyword.value = ''
  results.value = []
  selectedMembers.value = []
  searching.value = false
  searched.value = false
  errorMessage.value = ''
}

watch(
  () => props.open,
  (open) => {
    if (open) reset()
  },
)
</script>

<template>
  <AppDialog
    :open="open"
    title="新建群聊"
    confirm-text="创建群聊"
    :busy="submitting"
    wide
    @confirm="submit"
    @cancel="close"
  >
    <div class="new-group">
      <p v-if="errorMessage" class="new-group__error" role="alert">{{ errorMessage }}</p>

      <label class="new-group__label" for="new-group-title">
        群名称（必填，1~{{ GROUP_TITLE_MAX_LENGTH }} 字符）
      </label>
      <input
        id="new-group-title"
        v-model="title"
        type="text"
        :maxlength="GROUP_TITLE_MAX_LENGTH"
        class="new-group__input"
        placeholder="例如：研发群"
      />

      <form class="new-group__search" @submit.prevent="search">
        <input
          v-model="keyword"
          type="search"
          maxlength="64"
          class="new-group__input"
          placeholder="输入昵称或完整邮箱搜索同事"
          aria-label="搜索待邀请成员"
        />
        <button type="submit" class="new-group__search-btn" :disabled="searching">
          {{ searching ? '搜索中…' : '搜索' }}
        </button>
      </form>

      <p class="new-group__label">已选成员（{{ selectedMembers.length }}）</p>
      <p v-if="selectedMembers.length === 0" class="new-group__hint">
        至少选择 1 位成员（不含自己）。
      </p>
      <ul v-else class="new-group__chips">
        <li v-for="member in selectedMembers" :key="member.userId" class="new-group__chip">
          <span>{{ member.displayName }}</span>
          <button
            type="button"
            class="new-group__chip-remove"
            :aria-label="`移除 ${member.displayName}`"
            @click="removeSelected(member.userId)"
          >
            ×
          </button>
        </li>
      </ul>

      <ul v-if="results.length > 0" class="new-group__results">
        <li v-for="user in results" :key="user.userId" class="new-group__result">
          <Avatar
            :display-name="user.displayName"
            :avatar-file-id="user.avatarFileId"
            :size="28"
          />
          <span class="new-group__result-info">
            <span class="new-group__result-name">{{ user.displayName }}</span>
            <span v-if="user.email" class="new-group__result-email">{{ user.email }}</span>
          </span>
          <button type="button" class="new-group__toggle" @click="toggleMember(user)">
            {{ isSelected(user.userId) ? '取消选择' : '选择' }}
          </button>
        </li>
      </ul>
      <p v-else-if="searched && !searching" class="new-group__hint">没有找到匹配的用户。</p>
    </div>
  </AppDialog>
</template>

<style scoped>
.new-group {
  display: flex;
  flex-direction: column;
  gap: var(--ec-space-2);
}

.new-group__error {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-3);
  border-radius: var(--ec-radius-sm);
  background: var(--ec-danger);
  color: var(--ec-text-inverse);
  font-size: var(--ec-font-size-secondary);
}

.new-group__label {
  margin: var(--ec-space-1) 0 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.new-group__hint {
  margin: 0;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
}

.new-group__input {
  width: 100%;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-input);
  outline: none;
  box-sizing: border-box;
}

.new-group__input:focus {
  border-color: var(--ec-brand-600);
}

.new-group__search {
  display: flex;
  gap: var(--ec-space-2);
  margin-top: var(--ec-space-2);
}

.new-group__search-btn {
  flex: none;
  padding: 0 var(--ec-space-4);
  border: none;
  border-radius: var(--ec-radius-input);
  background: var(--ec-brand-600);
  color: var(--ec-text-inverse);
  cursor: pointer;
}

.new-group__chips {
  display: flex;
  flex-wrap: wrap;
  gap: var(--ec-space-2);
  margin: 0;
  padding: 0;
  list-style: none;
}

.new-group__chip {
  display: inline-flex;
  gap: var(--ec-space-1);
  align-items: center;
  padding: 2px var(--ec-space-2);
  border-radius: var(--ec-radius-full);
  background: var(--ec-brand-050);
  color: var(--ec-brand-600);
  font-size: var(--ec-font-size-secondary);
}

.new-group__chip-remove {
  padding: 0 2px;
  border: none;
  background: transparent;
  color: var(--ec-brand-600);
  font-size: 13px;
  cursor: pointer;
}

.new-group__results {
  max-height: 220px;
  margin: var(--ec-space-1) 0 0;
  padding: 0;
  overflow-y: auto;
  list-style: none;
}

.new-group__result {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
  padding: var(--ec-space-2) 0;
  border-bottom: 1px solid var(--ec-border);
}

.new-group__result-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.new-group__result-name {
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
}

.new-group__result-email {
  overflow: hidden;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.new-group__toggle {
  flex: none;
  padding: 4px 10px;
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-sm);
  background: transparent;
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-small);
  cursor: pointer;
}
</style>
