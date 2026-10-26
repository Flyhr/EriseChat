// UI-04 批次3：面板/预览的「先关面板再退页」历史保护。
// 面板打开时压入一个同 URL 的 history 哨兵；Android 返回键/浏览器后退先消费哨兵并关闭面板，
// 停在当前页面而不是直接退出会话。仅操作 history API，不改路由语义（URL 不变）。
export interface PanelHistoryGuard {
  /** 面板打开时调用：压入同 URL 哨兵并监听 popstate（幂等）。 */
  push(): void
  /** 面板关闭时调用：移除监听；若哨兵仍在则用 replaceState 标记失效，避免历史残留脏状态。 */
  dispose(): void
}

export function createPanelHistoryGuard(onBack: () => void): PanelHistoryGuard {
  let pushed = false

  function onPopState(): void {
    if (!pushed) return
    pushed = false
    onBack()
  }

  return {
    push() {
      if (pushed || typeof window === 'undefined') return
      window.history.pushState({ ...(window.history.state ?? {}), ecPanelGuard: true }, '')
      pushed = true
      window.addEventListener('popstate', onPopState)
    },
    dispose() {
      if (typeof window === 'undefined') return
      window.removeEventListener('popstate', onPopState)
      if (!pushed) return
      pushed = false
      window.history.replaceState({ ...(window.history.state ?? {}), ecPanelGuard: false }, '')
    },
  }
}
