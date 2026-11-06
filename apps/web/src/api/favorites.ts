// 个人收藏接口（F-38）：SQLite 为事实源，同账号跨设备同步。
// 列表只返回仍可访问的原消息快照；不可用条目 available=false 且 message=null。
import { http } from '../core/http'
import type { FavoriteItem } from '../types/frames'

export const favoritesApi = {
  list: (): Promise<FavoriteItem[]> => http.get<FavoriteItem[]>('/favorites'),

  /** 新增收藏（服务端幂等：已收藏直接返回现有条目，可安全重试）。 */
  add: (messageId: string): Promise<FavoriteItem> => http.post<FavoriteItem>('/favorites', { messageId }),

  /** 取消收藏（服务端幂等：不存在也返回 204，可安全重试）。 */
  remove: (messageId: string): Promise<void> =>
    http.delete<void>(`/favorites/${encodeURIComponent(messageId)}`),
}
