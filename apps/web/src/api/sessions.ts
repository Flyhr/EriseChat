// 登录会话 API（F-13；对应 server/Chat.Server/Api/SessionEndpoints.cs）。
// 列表只含当前用户未撤销的会话；remote 下号会软撤销并踢掉该会话的 WS 连接。
import { http } from '../core/http'

export interface SessionInfo {
  sessionId: string
  deviceId: string
  /** 登录时间（UTC 毫秒） */
  createdAt: number
  /** 最近活跃时间（UTC 毫秒） */
  lastSeenAt: number
  /** 是否为发起本次请求的会话（当前设备，不可踢） */
  current: boolean
}

export const sessionsApi = {
  /** 当前账号的登录设备列表（GET /api/sessions，按 lastSeenAt 降序） */
  list: () => http.get<SessionInfo[]>('/sessions'),

  /** 远程退出指定设备（DELETE /api/sessions/{sessionId}；幂等 204） */
  kick: (sessionId: string) => http.delete<void>(`/sessions/${encodeURIComponent(sessionId)}`),
}
