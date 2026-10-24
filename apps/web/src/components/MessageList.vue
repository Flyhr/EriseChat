<script setup lang="ts">
import type { Message } from '../types/frames'
import MessageBubble from './MessageBubble.vue'

// 消息列表（纯展示）：按 seq 升序渲染，自身消息靠右。
defineProps<{
  messages: readonly Message[]
  selfUserId?: string
}>()
</script>

<template>
  <div class="message-list">
    <MessageBubble
      v-for="message in messages"
      :key="message.messageId"
      :message="message"
      :self="message.senderId === selfUserId"
    />
  </div>
</template>

<style scoped>
.message-list {
  display: flex;
  flex-direction: column;
  gap: var(--space-3);
  padding: var(--space-4);
  overflow-y: auto;
}
</style>
