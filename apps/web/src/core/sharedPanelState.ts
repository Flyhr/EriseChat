// C2c：会话共享媒体面板的页签与滚动位置。
// 明细面板关闭时 ConversationFilesPanel 会被卸载；状态若放在组件实例内会随卸载丢失，
// 因此按 conversationId 保存在模块作用域（仅内存），重新打开面板时恢复筛选与位置。
export type SharedPanelTab = 'image' | 'link' | 'file'

export interface SharedPanelState {
  tab: SharedPanelTab
  scroll: Record<SharedPanelTab, number>
}

const states = new Map<string, SharedPanelState>()

/** 取（必要时创建）指定会话的面板状态。 */
export function sharedPanelStateOf(conversationId: string): SharedPanelState {
  let state = states.get(conversationId)
  if (!state) {
    state = { tab: 'image', scroll: { image: 0, link: 0, file: 0 } }
    states.set(conversationId, state)
  }
  return state
}

/** 记住当前页签，下次打开面板时直接回到该页签。 */
export function rememberSharedPanelTab(conversationId: string, tab: SharedPanelTab): void {
  if (!conversationId) return
  sharedPanelStateOf(conversationId).tab = tab
}

/** 记住当前页签的滚动位置（仅内存，不落盘）。 */
export function rememberSharedPanelScroll(
  conversationId: string,
  tab: SharedPanelTab,
  top: number,
): void {
  if (!conversationId) return
  sharedPanelStateOf(conversationId).scroll[tab] = Math.max(0, Math.round(top))
}
