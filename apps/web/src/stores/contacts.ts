import { ref } from 'vue'
import { defineStore } from 'pinia'

// 通讯录/建群候选。M2 接入服务端用户搜索（api/users.ts），M0 只做本地状态占位。
export interface ContactBrief {
  userId: string
  displayName: string
  email: string
  avatarFileId?: string
}

export const useContactsStore = defineStore('contacts', () => {
  const contacts = ref<ContactBrief[]>([])

  function setContacts(list: readonly ContactBrief[]): void {
    contacts.value = [...list]
  }

  function clear(): void {
    contacts.value = []
  }

  return {
    contacts,
    setContacts,
    clear,
  }
})
