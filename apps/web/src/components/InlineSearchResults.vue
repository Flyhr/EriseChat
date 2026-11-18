<script setup lang="ts">
import { computed } from 'vue'
import type { UserProfile } from '../api/users'
import type { InlineSearchCategory } from '../core/inlineSearch'
import { conversationTitle, otherMemberOf } from '../core/conversationTitle'
import type { Conversation, Message } from '../types/frames'
import Avatar from './Avatar.vue'
import EmptyState from './EmptyState.vue'
import SearchResultItem from './SearchResultItem.vue'

// 列表内联搜索的结果区（消息页 / 联系人页共用）：分类 chips + 分组结果 + 空态。
// 数据由 useInlineSearch 提供；本组件只负责展示与点击转发，不做请求。
const props = withDefaults(
  defineProps<{
    query: string
    category: InlineSearchCategory
    loading: boolean
    error: string
    conversations: readonly Conversation[]
    contacts: readonly UserProfile[]
    strangers: readonly UserProfile[]
    messages: readonly Message[]
    selfUserId: string
    /** 消息页需要「会话」分组；联系人页不展示会话命中。 */
    showConversations?: boolean
    /** 联系人页展示行内的关系操作（加好友 / 已发送 / 待处理 / 已好友）。 */
    showRelationActions?: boolean
    /** 当前选中用户（联系人页右侧资料区）。 */
    activeUserId?: string
    /** 好友备注取值：非空时优先作为会话行标题。 */
    remarkOf?: (userId: string) => string | undefined
  }>(),
  { showConversations: false, showRelationActions: false, activeUserId: '' },
)

const emit = defineEmits<{
  'update:category': [category: InlineSearchCategory]
  'select-conversation': [conversationId: string]
  'select-message': [message: Message]
  'select-user': [user: UserProfile]
  'request-friend': [userId: string]
}>()

const categories: { key: InlineSearchCategory; label: string }[] = [
  { key: 'all', label: '全部' },
  { key: 'contacts', label: '联系人' },
  { key: 'messages', label: '聊天信息' },
  { key: 'strangers', label: '陌生人' },
]

const showConversationGroup = computed(
  () => props.showConversations && props.category === 'all' && props.conversations.length > 0,
)
const showContactGroup = computed(
  () =>
    (props.category === 'all' || props.category === 'contacts') && props.contacts.length > 0,
)
const showMessageGroup = computed(
  () =>
    (props.category === 'all' || props.category === 'messages') && props.messages.length > 0,
)
const showStrangerGroup = computed(
  () => props.category === 'strangers' && props.strangers.length > 0,
)

const visibleCount = computed(() => {
  if (props.category === 'contacts') return props.contacts.length
  if (props.category === 'messages') return props.messages.length
  if (props.category === 'strangers') return props.strangers.length
  return (
    (props.showConversations ? props.conversations.length : 0) +
    props.contacts.length +
    props.messages.length
  )
})

const showEmpty = computed(
  () => !props.loading && !props.error && visibleCount.value === 0,
)

const emptyText = '没有匹配结果'

function conversationName(conversation: Conversation): string {
  return conversationTitle(conversation, props.selfUserId, { groupStyle: 'plain', remarkOf: props.remarkOf })
}

function conversationAvatar(conversation: Conversation): string | undefined {
  if (conversation.type === 'group') return conversation.avatarFileId ?? undefined
  return otherMemberOf(conversation, props.selfUserId)?.avatarFileId ?? undefined
}

function conversationMeta(conversation: Conversation): string {
  if (conversation.type === 'group') return `群聊 · ${conversation.members.length} 人`
  return '单聊'
}

function relationText(user: UserProfile): string {
  if (user.relation === 'friend') return '已好友'
  if (user.relation === 'outgoing') return '已发送'
  if (user.relation === 'incoming') return '待处理'
  return '加好友'
}

function onRelationClick(user: UserProfile): void {
  if (user.relation === 'outgoing') return
  if (user.relation === undefined) {
    emit('request-friend', user.userId)
    return
  }
  emit('select-user', user)
}
</script>

<template>
  <div class="inline-search" role="region" aria-label="搜索结果">
    <div class="inline-search__chips" role="tablist" aria-label="搜索结果分类">
      <button
        v-for="item in categories"
        :key="item.key"
        type="button"
        role="tab"
        class="inline-search__chip"
        :class="{ 'inline-search__chip--active': category === item.key }"
        :aria-selected="category === item.key"
        @click="emit('update:category', item.key)"
      >
        {{ item.label }}
      </button>
    </div>

    <div class="inline-search__body">
      <p v-if="error" class="inline-search__error" role="alert">{{ error }}</p>
      <p v-else-if="loading" class="inline-search__hint">搜索中…</p>

      <template v-if="!error && !loading">
        <section v-if="showConversationGroup" class="inline-search__group" aria-label="会话">
          <h3 class="inline-search__group-title">会话 <span>{{ conversations.length }}</span></h3>
          <ul class="inline-search__list">
            <li v-for="conversation in conversations" :key="conversation.conversationId">
              <button
                type="button"
                class="inline-search__row"
                @click="emit('select-conversation', conversation.conversationId)"
              >
                <Avatar
                  :display-name="conversationName(conversation)"
                  :size="36"
                  :avatar-file-id="conversationAvatar(conversation)"
                />
                <span class="inline-search__row-info">
                  <span class="inline-search__row-name">{{ conversationName(conversation) }}</span>
                  <span class="inline-search__row-meta">{{ conversationMeta(conversation) }}</span>
                </span>
              </button>
            </li>
          </ul>
        </section>

        <section v-if="showMessageGroup" class="inline-search__group" aria-label="聊天信息">
          <h3 class="inline-search__group-title">聊天信息 <span>{{ messages.length }}</span></h3>
          <ul class="inline-search__list" role="listbox" aria-label="聊天信息搜索结果">
            <li v-for="message in messages" :key="message.messageId">
              <SearchResultItem
                :message="message"
                :query="query"
                @select="emit('select-message', $event)"
              />
            </li>
          </ul>
        </section>

        <section v-if="showContactGroup" class="inline-search__group" aria-label="联系人">
          <h3 class="inline-search__group-title">联系人 <span>{{ contacts.length }}</span></h3>
          <ul class="inline-search__list">
            <li v-for="user in contacts" :key="user.userId" class="inline-search__row-wrap">
              <button
                type="button"
                class="inline-search__row"
                :class="{ 'inline-search__row--active': activeUserId === user.userId }"
                @click="emit('select-user', user)"
              >
                <Avatar
                  :display-name="user.displayName"
                  :size="36"
                  :avatar-file-id="user.avatarFileId ?? undefined"
                />
                <span class="inline-search__row-info">
                  <span class="inline-search__row-name">{{ user.displayName }}</span>
                  <span v-if="user.email" class="inline-search__row-meta">{{ user.email }}</span>
                </span>
              </button>
              <button
                v-if="showRelationActions"
                type="button"
                class="inline-search__relation"
                :class="{
                  'inline-search__relation--primary': user.relation === undefined,
                }"
                :disabled="user.relation === 'outgoing'"
                @click="onRelationClick(user)"
              >
                {{ relationText(user) }}
              </button>
            </li>
          </ul>
        </section>

        <section v-if="showStrangerGroup" class="inline-search__group" aria-label="陌生人">
          <h3 class="inline-search__group-title">陌生人 <span>{{ strangers.length }}</span></h3>
          <ul class="inline-search__list">
            <li v-for="user in strangers" :key="user.userId">
              <button
                type="button"
                class="inline-search__row"
                :class="{ 'inline-search__row--active': activeUserId === user.userId }"
                @click="emit('select-user', user)"
              >
                <Avatar :display-name="user.displayName" :size="36" />
                <span class="inline-search__row-info">
                  <span class="inline-search__row-name">{{ user.displayName }}</span>
                </span>
              </button>
            </li>
          </ul>
        </section>

        <EmptyState v-if="showEmpty" class="inline-search__empty" icon="search" :title="emptyText" />
      </template>
    </div>
  </div>
</template>

<style scoped>
.inline-search {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

.inline-search__chips {
  display: flex;
  flex: none;
  gap: var(--ec-space-1);
  padding: var(--ec-space-3) var(--ec-space-4);
  overflow-x: auto;
}

.inline-search__chip {
  flex: none;
  padding: 5px var(--ec-space-3);
  border: 1px solid transparent;
  border-radius: var(--ec-radius-full);
  background: var(--ec-nav-bg);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
  transition:
    background var(--ec-transition-fast) var(--ec-ease),
    color var(--ec-transition-fast) var(--ec-ease);
}

@media (hover: hover) and (pointer: fine) {
  .inline-search__chip:hover {
    background: var(--ec-surface-hover);
    color: var(--ec-text-primary);
  }
}

.inline-search__chip--active,
.inline-search__chip--active:hover {
  background: var(--ec-brand-050);
  color: var(--ec-brand-700);
  font-weight: var(--ec-font-weight-semibold);
}

.inline-search__chip:focus-visible,
.inline-search__row:focus-visible,
.inline-search__relation:focus-visible {
  outline: var(--ec-focus-width) solid var(--ec-focus-ring);
  outline-offset: var(--ec-focus-offset);
}

.inline-search__body {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding-bottom: var(--ec-space-3);
}

.inline-search__group-title {
  margin: 0;
  padding: var(--ec-space-2) var(--ec-space-4) var(--ec-space-1);
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  font-weight: var(--ec-font-weight-medium);
}

.inline-search__group-title span {
  margin-left: var(--ec-space-1);
  color: var(--ec-text-tertiary);
}

.inline-search__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0 var(--ec-space-2);
  list-style: none;
}

.inline-search__row-wrap {
  display: flex;
  gap: var(--ec-space-2);
  align-items: center;
}

.inline-search__row {
  display: flex;
  flex: 1;
  gap: var(--ec-space-3);
  align-items: center;
  min-width: 0;
  min-height: 56px;
  padding: var(--ec-space-2) var(--ec-space-3);
  border: none;
  border-radius: var(--ec-radius-sm);
  background: transparent;
  text-align: left;
  cursor: pointer;
}

@media (hover: hover) and (pointer: fine) {
  .inline-search__row:hover {
    background: var(--ec-surface-hover);
  }
}

.inline-search__row--active {
  background: var(--ec-brand-050);
}

.inline-search__row-info {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.inline-search__row-name {
  overflow: hidden;
  color: var(--ec-text-primary);
  font-size: var(--ec-font-size-body);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.inline-search__row-meta {
  overflow: hidden;
  color: var(--ec-text-tertiary);
  font-size: var(--ec-font-size-small);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.inline-search__relation {
  flex: none;
  min-height: 32px;
  padding: 0 var(--ec-space-3);
  border: 1px solid var(--ec-border);
  border-radius: var(--ec-radius-full);
  background: var(--ec-surface);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  cursor: pointer;
}

.inline-search__relation--primary {
  border-color: var(--ec-brand-500);
  color: var(--ec-brand-600);
}

.inline-search__relation:disabled {
  color: var(--ec-text-tertiary);
  cursor: default;
}

.inline-search__hint {
  margin: var(--ec-space-3) var(--ec-space-4);
  color: var(--ec-text-secondary);
  font-size: var(--ec-font-size-secondary);
  line-height: var(--ec-line-height-body);
}

.inline-search__error {
  margin: var(--ec-space-3) var(--ec-space-4);
  color: var(--ec-danger);
  font-size: var(--ec-font-size-secondary);
}

.inline-search__empty {
  margin-top: var(--ec-space-4);
}

@media (max-width: 800px) {
  .inline-search__chip {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-height: var(--ec-touch-target);
    padding: 0 var(--ec-space-4);
    font-size: var(--ec-font-size-mobile-list-sub);
  }
}
</style>
