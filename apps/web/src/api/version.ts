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
  /**
   * 2026-11-12 追加的可选字段（协议 §7 第 10 条）。
   * 声明为可选：客户端可能连到尚未升级的旧服务端，缺失时按「无」处理。
   */
  /** 当前部署的 Web 构建版本，来自 web-version.json；缺失时为 null。 */
  webVersion?: string | null
  /** 完整发行的最低登录版本；缺失、非法或高于发行版本时为 null。 */
  minVersion?: string | null
  /** 当前发行的纯文本说明，最多 10 条。 */
  releaseNotes?: string[]
  /** 发行时间（UTC 毫秒），缺失时为 null。 */
  publishedAt?: number | null
}

export function fetchVersionManifest(): Promise<VersionManifest> {
  // 路径不带 /api：request() 会拼上 resolveApiBase()（同源或远端服务器地址 + /api）。
  // 清单匿名可读：auth=false 不附带登录令牌，401 也不触发统一注销，失败只影响检查状态。
  return request<VersionManifest>('/version', { auth: false })
}
