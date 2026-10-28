// 本机存储访问统一入口（P2 清理）：原先 7 处模块各写一遍「隐私模式/禁用存储会抛异常」的 try/catch。
// 约定：读取失败返回 null，写入失败返回 false/静默忽略 —— 调用方据此降级为仅内存，不阻塞主流程。

/** 取 localStorage；不存在（SSR/Node 测试）或访问被拒（隐私模式）时返回 null。 */
export function getStorage(): Storage | null {
  try {
    return typeof localStorage === 'undefined' ? null : localStorage
  } catch {
    return null
  }
}

/** 读取字符串；存储不可用或读取异常时返回 null。 */
export function readStorage(key: string): string | null {
  try {
    return getStorage()?.getItem(key) ?? null
  } catch {
    return null
  }
}

/** 写入字符串；成功返回 true，存储不可用/配额满返回 false。 */
export function writeStorage(key: string, value: string): boolean {
  try {
    const storage = getStorage()
    if (storage === null) return false
    storage.setItem(key, value)
    return true
  } catch {
    return false
  }
}

/** 删除键；存储不可用时静默忽略（本来也没有持久化）。 */
export function removeStorage(key: string): void {
  try {
    getStorage()?.removeItem(key)
  } catch {
    // 忽略：存储不可用时无内容可删。
  }
}
