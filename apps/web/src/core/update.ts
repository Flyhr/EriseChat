// 客户端更新检查的纯函数层（F-29，plan §F-29）：
// 版本比较、是否需要提示更新、按平台取产物、生成更新提示；不依赖网络/DOM，便于单测。

import type { VersionArtifact, VersionManifest } from '../api/version'

export type UpdatePlatform = 'desktop' | 'android'

/** 更新提示：available=false 时 latest 为当前清单版本（供界面展示「已是最新」）。 */
export interface UpdatePrompt {
  available: boolean
  latest: string
  url: string
  sha256: string
}

/** 下载进度（桌面壳 IPC / Android 插件回传）。 */
export interface UpdateProgress {
  downloaded: number
  total: number
  percent: number
}

/** 桌面壳检查更新的结果（IPC update:check）。 */
export interface DesktopUpdateCheckResult {
  available: boolean
  currentVersion: string
  latestVersion: string
  artifact: VersionArtifact | null
}

function parseVersionSegment(segment: string): number {
  const value = Number.parseInt(segment, 10)
  return Number.isFinite(value) ? value : 0
}

/**
 * 比较 x.y.z：返回 1 表示 a 更高、-1 表示 a 更低、0 表示相同。
 * 非法/缺失段按 0 处理，多余段忽略（v1.2.3.4 视为 1.2.3）。
 */
export function compareVersions(a: string, b: string): number {
  const left = a.split('.')
  const right = b.split('.')
  for (let index = 0; index < 3; index += 1) {
    const difference = parseVersionSegment(left[index] ?? '0') - parseVersionSegment(right[index] ?? '0')
    if (difference !== 0) return difference > 0 ? 1 : -1
  }
  return 0
}

/** 清单版本比当前版本更高时才提示更新；空 latest 不提示。 */
export function shouldOfferUpdate(current: string, latest: string): boolean {
  return latest.trim().length > 0 && compareVersions(latest, current) > 0
}

/** 取指定平台的产物；清单缺失或该平台无产物时返回 null。 */
export function platformArtifactOf(
  manifest: VersionManifest | null | undefined,
  platform: UpdatePlatform,
): VersionArtifact | null {
  return manifest?.artifacts?.[platform] ?? null
}

/**
 * 生成指定平台的更新提示：
 * - 平台产物存在、sha256 非空且版本更高 → available=true；
 * - 否则 available=false（latest 回退到当前版本，不用清单总版本误导「已是最新」）。
 */
export function updatePromptOf(
  manifest: VersionManifest | null | undefined,
  currentVersion: string,
  platform: UpdatePlatform,
): UpdatePrompt {
  const artifact = platformArtifactOf(manifest, platform)
  const latest = artifact?.version ?? currentVersion
  const available =
    artifact !== null &&
    artifact.sha256.trim().length > 0 &&
    shouldOfferUpdate(currentVersion, artifact.version)
  return {
    available,
    latest,
    url: artifact?.url ?? '',
    sha256: artifact?.sha256 ?? '',
  }
}

/**
 * 浏览器端更新提示：按当前部署的 Web 构建版本（webVersion）判断。
 * 缺少 webVersion（旧服务端或未部署 web-version.json）时不提示，避免把服务端或安装包版本当成 Web 版本。
 */
export function webUpdateOf(
  manifest: VersionManifest | null | undefined,
  currentVersion: string,
): { available: boolean; latest: string } {
  const latest = manifest?.webVersion?.trim() ?? ''
  if (latest.length === 0) {
    return { available: false, latest: currentVersion }
  }
  return { available: shouldOfferUpdate(currentVersion, latest), latest }
}
