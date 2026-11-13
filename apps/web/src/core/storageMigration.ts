// 1.2.0 命名迁移：把旧键内容一次性搬到新键，之后业务代码只读新键，不保留长期双读。
import { clearLegacySavedLogin } from './savedCredentials'
import { readStorage, removeStorage, writeStorage } from './safeStorage'

const LEGACY_PREFIX = 'eisechat.'
const CURRENT_PREFIX = 'erisechat.'

/** 7 个本机存储键的后缀，与 core/ 与 stores/ 中的定义一一对应。
 *  savedLogin 不参与迁移：它是历史明文记密，只清理不搬移（见 clearLegacySavedLogin）。 */
const KEY_SUFFIXES = [
  'session',
  'settings',
  'drafts.v1',
  'deviceId',
  'readSeq',
  'emojiRecent.v1',
  'serverUrl',
] as const

/** 新键已存在时不覆盖；旧键在新键就绪后删除，写入失败则保留旧值等待下次启动。 */
export function migrateLegacyStorage(): void {
  for (const suffix of KEY_SUFFIXES) {
    const oldKey = LEGACY_PREFIX + suffix
    const newKey = CURRENT_PREFIX + suffix
    const value = readStorage(oldKey)
    if (value === null) continue
    if (readStorage(newKey) !== null || writeStorage(newKey, value)) {
      removeStorage(oldKey)
    }
  }
}

// 导入即执行：作为 main.ts 的第一个 import，保证在任何模块读取存储之前完成清理与迁移。
// 顺序不可反：若先迁移，历史 savedLogin 明文会被搬到新键重新落地。
clearLegacySavedLogin()
migrateLegacyStorage()
