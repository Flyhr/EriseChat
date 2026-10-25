import { ref } from 'vue'
import { defineStore } from 'pinia'
import { chatWs, type WsState } from '../core/ws'
import { updatePresence } from '../core/presence'
import type { ServerFrame } from '../types/frames'

// 在线状态（M3-09）：由服务端 presence 帧驱动（0→1 在线、1→0 离线，只推给有共同会话的在线用户）。
// v1 无初始查询接口：未收到过推送的用户为「未知」（不要显示成离线）；登出/切账号/断连清空（历史状态丢失为 v1 限制）。
export const usePresenceStore = defineStore('presence', () => {
  const onlineByUser = ref<Record<string, boolean>>({})
  const unsubscribers: Array<() => void> = []
  let subscribed = false

  /** 查询已知在线状态：true 在线、false 离线、undefined 未知。 */
  function stateOf(userId: string | undefined): boolean | undefined {
    if (!userId) return undefined
    return onlineByUser.value[userId]
  }

  function clear(): void {
    onlineByUser.value = {}
  }

  /** 订阅 presence 帧（只注册一次）；连接离开 authed 状态时清空（断线期间状态不可信）。 */
  function subscribeRealtime(): () => void {
    if (subscribed) return dispose
    subscribed = true
    unsubscribers.push(
      chatWs.on('frame', (frame: ServerFrame) => {
        if (frame.type !== 'presence') return
        onlineByUser.value = updatePresence(onlineByUser.value, frame.userId, frame.online)
      }),
    )
    unsubscribers.push(
      chatWs.on('statechange', (state: WsState) => {
        if (state !== 'authed') clear()
      }),
    )
    return dispose
  }

  function dispose(): void {
    for (const unsubscribe of unsubscribers.splice(0)) unsubscribe()
    clear()
    subscribed = false
  }

  return {
    onlineByUser,
    stateOf,
    clear,
    subscribeRealtime,
  }
})
