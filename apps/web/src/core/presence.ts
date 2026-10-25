// 在线状态纯逻辑（M3-09）：presence 帧驱动的在线状态展示。
// v1 没有「查询在线状态」接口，只根据 0→1 / 1→0 推送更新；未知状态不显示成「离线」。
export type PresenceState = boolean | undefined

/** 在线状态文案：true 在线、false 离线、undefined 未知。 */
export function presenceLabelOf(state: PresenceState): '在线' | '离线' | '未知' {
  if (state === true) return '在线'
  if (state === false) return '离线'
  return '未知'
}

/** 更新某用户的在线状态（只记录收到推送的用户；未知用户保持 undefined）。 */
export function updatePresence(
  map: Readonly<Record<string, boolean>>,
  userId: string,
  online: boolean,
): Record<string, boolean> {
  if (!userId) return { ...map }
  return { ...map, [userId]: online }
}
