/**
 * F-31：打包壳登录页的服务器地址预设（本地调试 / 服务器一键切换）。
 *
 * 默认值用于当前内测环境；可用构建期环境变量 `VITE_SERVER_PRESETS`
 * （逗号分隔的「标签=地址」）覆盖，例如：
 *   VITE_SERVER_PRESETS=服务器=https://chat.example.com,本机调试=http://127.0.0.1:6580
 */
export interface ServerPreset {
  /** 展示标签（如「服务器」「本机调试」） */
  label: string
  /** 规范化后的 origin（与 localStorage 中保存的地址口径一致） */
  url: string
}

const DEFAULT_SERVER_PRESETS: readonly ServerPreset[] = [
  { label: '服务器（Tailscale HTTPS）', url: 'https://chat-server.tail8c07f5.ts.net' },
  { label: '本机调试（USB 端口映射）', url: 'http://127.0.0.1:6580' },
]

/** 只接受 http(s) 且带主机名的地址，统一为 origin（去 path/query/尾部斜杠）。 */
function normalizePresetUrl(raw: string): string | null {
  try {
    const url = new URL(raw.trim())
    if (url.protocol !== 'http:' && url.protocol !== 'https:') return null
    if (url.hostname.length === 0) return null
    return url.origin
  } catch {
    return null
  }
}

/** 解析 `标签=地址` 列表；空串/全部非法时回退默认预设。 */
export function parseServerPresets(
  raw: string | undefined,
  fallback: readonly ServerPreset[] = DEFAULT_SERVER_PRESETS,
): readonly ServerPreset[] {
  if (!raw || raw.trim().length === 0) return fallback
  const items: ServerPreset[] = []
  for (const entry of raw.split(',')) {
    const separator = entry.indexOf('=')
    if (separator <= 0) continue
    const label = entry.slice(0, separator).trim()
    const url = normalizePresetUrl(entry.slice(separator + 1))
    if (label.length === 0 || !url) continue
    items.push({ label, url })
  }
  return items.length > 0 ? items : fallback
}

/** 按 origin 匹配预设（用于回显当前地址对应的下拉选项）。 */
export function findServerPreset(
  presets: readonly ServerPreset[],
  url: string,
): ServerPreset | undefined {
  const normalized = normalizePresetUrl(url)
  if (!normalized) return undefined
  return presets.find((preset) => preset.url === normalized)
}

/** 默认预设（未设置 VITE_SERVER_PRESETS 时的兜底，供文档/测试引用）。 */
export const DEFAULT_SERVER_PRESET_LIST: readonly ServerPreset[] = DEFAULT_SERVER_PRESETS
