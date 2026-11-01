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
