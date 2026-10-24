// 管理接口（对应 server/Chat.Server/Api/AdminEndpoints.cs；仅 admin 角色可调用，M3 实现）。
import { http } from '../core/http'
import type { UserProfile } from './users'

export interface AuditLogEntry {
  id: string
  adminId: string
  action: string
  target: string
  detail?: unknown
  createdAt: number
}

export const adminApi = {
  listUsers: (keyword?: string) =>
    http.get<{ users: UserProfile[] }>('/admin/users', { query: { keyword } }),

  disableUser: (userId: string) => http.post<void>(`/admin/users/${userId}/disable`),

  enableUser: (userId: string) => http.post<void>(`/admin/users/${userId}/enable`),

  auditLogs: (limit = 50) => http.get<AuditLogEntry[]>('/admin/audit-logs', { query: { limit } }),
}
