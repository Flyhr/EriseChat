// 草稿状态（F-04，阶段 A｜纯 Web）：按会话保存输入中文本，切换会话/刷新后恢复。
// 持久化走 core/drafts.ts（localStorage），失败静默降级；持久化按会话 300ms 防抖，页面隐藏/卸载时立即 flush。
import { ref } from 'vue'
import { defineStore } from 'pinia'
import { MAX_TEXT_LENGTH } from '../core/config'
import {
  DRAFTS_STORAGE_KEY,
  pruneDrafts,
  readDrafts,
  writeDrafts,
  type DraftEntry,
  type DraftMap,
} from '../core/drafts'

/** 单会话草稿持久化防抖（毫秒）：输入过程中只在停顿后写一次 localStorage。 */
export const DRAFT_DEBOUNCE_MS = 300

export const useDraftsStore = defineStore('drafts', () => {
  /** 内存中的草稿表（初始化时从 localStorage 恢复并按容量裁剪一次）。 */
  const drafts = ref<DraftMap>(pruneDrafts(readDrafts()))
  /** conversationId → 待持久化 timer。 */
  const timers = new Map<string, ReturnType<typeof setTimeout>>()

  function clearTimer(conversationId: string): void {
    const timer = timers.get(conversationId)
    if (timer === undefined) return
    clearTimeout(timer)
    timers.delete(conversationId)
  }

  /** 写盘前统一裁剪到最近 20 个会话，并同步内存，避免 localStorage 无限增长。 */
  function persist(): void {
    const pruned = pruneDrafts(drafts.value)
    drafts.value = pruned
    writeDrafts(pruned)
  }

  /** 返回去掉某会话后的新草稿表（不修改入参）。 */
  function withoutDraft(conversationId: string): DraftMap {
    const next: DraftMap = {}
    for (const [key, value] of Object.entries(drafts.value)) {
      if (key !== conversationId) next[key] = value
    }
    return next
  }

  function draftOf(conversationId: string): string {
    return drafts.value[conversationId]?.text ?? ''
  }

  /**
   * 更新某会话草稿：立即更新内存，300ms 后持久化；空串移除并立即写盘；
   * 超过 MAX_TEXT_LENGTH 的输入不保存（正常输入已被输入框 maxlength 限制）。
   */
  function setDraft(conversationId: string, text: string): void {
    if (!conversationId) return
    const value = typeof text === 'string' ? text : ''
    clearTimer(conversationId)
    if (value.length === 0) {
      if (drafts.value[conversationId]) {
        drafts.value = withoutDraft(conversationId)
        persist()
      }
      return
    }
    if (value.length > MAX_TEXT_LENGTH) return
    const entry: DraftEntry = { text: value, updatedAt: Date.now() }
    drafts.value = { ...drafts.value, [conversationId]: entry }
    timers.set(
      conversationId,
      setTimeout(() => {
        timers.delete(conversationId)
        persist()
      }, DRAFT_DEBOUNCE_MS),
    )
  }

  /** 立即持久化某会话草稿（切换会话前调用，避免防抖窗口内丢内容）。 */
  function flush(conversationId: string): void {
    clearTimer(conversationId)
    persist()
  }

  /** 立即持久化全部草稿（页面隐藏/卸载时调用）。 */
  function flushAll(): void {
    for (const timer of timers.values()) clearTimeout(timer)
    timers.clear()
    persist()
  }

  /** 删除某会话草稿并立即写盘（发送成功后调用）。 */
  function removeDraft(conversationId: string): void {
    clearTimer(conversationId)
    if (!drafts.value[conversationId]) return
    drafts.value = withoutDraft(conversationId)
    persist()
  }

  /** 退出登录/切换账号：清 timer、清内存并删除 localStorage 键，保证不串账号。 */
  function clear(): void {
    for (const timer of timers.values()) clearTimeout(timer)
    timers.clear()
    drafts.value = {}
    try {
      localStorage.removeItem(DRAFTS_STORAGE_KEY)
    } catch {
      // 隐私模式/存储禁用：忽略。
    }
  }

  /** 页面隐藏或即将卸载时立即落盘（浏览器可能直接丢弃未完成的防抖 timer）。 */
  function flushOnHide(): void {
    flushAll()
  }

  function onVisibilityChange(): void {
    if (typeof document !== 'undefined' && document.visibilityState === 'hidden') flushAll()
  }

  // 模块/应用生命周期勾子只注册一次（store 为单例）；隐私模式下监听注册失败也静默忽略。
  try {
    if (typeof window !== 'undefined') {
      window.addEventListener('pagehide', flushOnHide)
      document.addEventListener('visibilitychange', onVisibilityChange)
    }
  } catch {
    // ignore
  }

  return { drafts, draftOf, setDraft, flush, flushAll, removeDraft, clear }
})
