// 通话接口（对应 server/Chat.Server/Api/CallEndpoints.cs）。
import { http } from '../core/http'

/** 服务端下发的 ICE 服务器（TURN 使用短期凭证，见 protocol.md §3.1）。 */
export interface IceServerDto {
  urls: string[]
  username: string
  credential: string
}

export interface IceServersResponse {
  iceServers: IceServerDto[]
}

/** 通话门槛（GET /api/calls/can-call/{conversationId}）：好友直接放行，否则需双方都发过消息。 */
export interface CanCallResponse {
  allowed: boolean
}

export const callsApi = {
  /** 取当前用户的 ICE 服务器（GET /api/calls/ice-servers）；未配置 TURN 时返回空数组。 */
  iceServers: () => http.get<IceServersResponse>('/calls/ice-servers'),

  /** 查询能否在该单聊发起通话；服务端在接通时还会再校验一次。 */
  canCall: (conversationId: string) =>
    http.get<CanCallResponse>(`/calls/can-call/${encodeURIComponent(conversationId)}`),
}
