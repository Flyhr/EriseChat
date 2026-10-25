import { ref } from 'vue'
import { defineStore } from 'pinia'

// 通讯录/建群候选（M2-04 由 Contacts 页面直接调用 usersApi.search；本 store 留给联系人缓存 M3+）。
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
