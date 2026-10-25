// 鉴权图片/文件下载的 Blob URL 缓存（M3-07）：同一 fileId 只下载一次，供缩略图/预览/下载复用。
// 仅内存缓存（不写 IndexedDB）；上限 100 个，超限 revoke 最旧的 URL；退出登录/被踢时整体清理，
// 避免不同账号之间看到上一账号的内容。

const MAX_CACHED_URLS = 100

/** fileId → object URL（保留插入顺序，Map 迭代顺序即最旧优先）。 */
const cachedUrls = new Map<string, string>()

/** 同一 fileId 的并发下载合并为一个 Promise，避免重复请求。 */
const inFlight = new Map<string, Promise<string>>()

/** 释放并移除全部缓存（退出登录/被踢/切换账号时调用）。 */
export function clearBlobCache(): void {
  for (const url of cachedUrls.values()) {
    URL.revokeObjectURL(url)
  }
  cachedUrls.clear()
}

/**
 * 取 fileId 对应的 object URL；未缓存时用 loader 下载 Blob 后创建 URL。
 * 下载失败时 Promise reject（调用方显示「图片不可用/重试」）。
 */
export function getOrLoadBlobUrl(fileId: string, loader: () => Promise<Blob>): Promise<string> {
  const cached = cachedUrls.get(fileId)
  if (cached) return Promise.resolve(cached)

  const pending = inFlight.get(fileId)
  if (pending) return pending

  const task = loader()
    .then((blob) => {
      const url = URL.createObjectURL(blob)
      cachedUrls.set(fileId, url)
      trimCache()
      return url
    })
    .finally(() => {
      inFlight.delete(fileId)
    })

  inFlight.set(fileId, task)
  return task
}

/** 超过上限时 revoke 最旧的 URL。 */
function trimCache(): void {
  while (cachedUrls.size > MAX_CACHED_URLS) {
    const oldestKey = cachedUrls.keys().next().value as string | undefined
    if (oldestKey === undefined) return
    const url = cachedUrls.get(oldestKey)
    cachedUrls.delete(oldestKey)
    if (url) URL.revokeObjectURL(url)
  }
}
