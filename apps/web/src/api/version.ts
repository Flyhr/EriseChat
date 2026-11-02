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
  return request<VersionManifest>('/api/version')
}
