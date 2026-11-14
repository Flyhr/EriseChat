// F-28：语音播放的全局互斥（同一时间只允许一条语音在播放）。
// 组件自行管理 Audio 元素与进度；这里仅保存「当前正在播放的停止函数」，
// 新的一条开始播放前先停掉上一条，避免多条语音叠音。

let activeStop: (() => void) | null = null

/** 认领播放权：若已有其它语音在播放，先停止它，再登记本次的停止函数。 */
export function claimVoicePlayback(stop: () => void): void {
  if (activeStop && activeStop !== stop) activeStop()
  activeStop = stop
}

/** 释放播放权（仅当登记的就是自己时清空，避免误清刚认领的其它播放）。 */
export function releaseVoicePlayback(stop: () => void): void {
  if (activeStop === stop) activeStop = null
}

/** 停止当前播放（例如切换会话/卸载页面时调用）。 */
export function stopActiveVoicePlayback(): void {
  if (activeStop) {
    const stop = activeStop
    activeStop = null
    stop()
  }
}

// C2b：登录期间记录每条语音的最近播放位置（以 fileId 为键，仅内存、不落盘）。
// 切换会话或暂停时写入，重新进入该会话后点击播放从记录位置继续；播放结束即清除。
const voicePositions = new Map<string, number>()

/** 记住播放位置（毫秒）；非正数视为已回到起点，直接清除记录。 */
export function rememberVoicePosition(fileId: string, positionMs: number): void {
  if (!fileId || !Number.isFinite(positionMs) || positionMs <= 0) {
    voicePositions.delete(fileId)
    return
  }
  voicePositions.set(fileId, Math.round(positionMs))
}

/** 读取播放位置（毫秒）；没有记录返回 0。 */
export function recallVoicePosition(fileId: string): number {
  return voicePositions.get(fileId) ?? 0
}

/** 清除播放位置（播完、出错或拖动回起点时调用）。 */
export function clearVoicePosition(fileId: string): void {
  voicePositions.delete(fileId)
}
