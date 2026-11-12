// 客户端更新清单（F-29，protocol/protocol.md §7 第 10 条）：
// GET /api/version 匿名返回服务端版本与各平台最新安装包产物（URL 同源、sha256 供下载后校验）。

import { request } from '../core/http'

/** 单个平台的安装包产物。 */
export interface VersionArtifact {
  version: string
  fileName: string
  url: string
  size: number
  sha256: string
  modifiedAt: number
}

/** GET /api/version 响应；某平台暂无产物时对应值为 null。 */
export interface VersionManifest {
  version: string
  protocolVersion: number
  artifacts: {
    desktop: VersionArtifact | null
    android: VersionArtifact | null
  }
}

export function fetchVersionManifest(): Promise<VersionManifest> {
  // 路径不带 /api：request() 会拼上 resolveApiBase()（同源或远端服务器地址 + /api）。
  // 清单匿名可读：auth=false 不附带登录令牌，401 也不触发统一注销，失败只影响检查状态。
  return request<VersionManifest>('/version', { auth: false })
}
