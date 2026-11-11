// 建群表单校验（UI-03 批次2）：从 Contacts/新建群聊弹窗抽出的纯逻辑，便于单测。
// 规则与协议/REST 校验保持一致：群名称 1~50 字符（去首尾空白后计），至少选择 1 位成员（不含自己）。
import { GROUP_TITLE_MAX_LENGTH } from './groupProfile'

// 群名长度上限与群资料编辑共用同一常量（core/groupProfile.ts），避免两处漂移。
export { GROUP_TITLE_MAX_LENGTH }
export const GROUP_MEMBERS_MIN_COUNT = 1

/** 去重后的成员 id（保持首次出现顺序）；空字符串与重复项会被忽略。 */
export function uniqueMemberIds(memberIds: readonly string[]): string[] {
  const seen = new Set<string>()
  const result: string[] = []
  for (const id of memberIds) {
    const trimmed = id.trim()
    if (trimmed.length === 0 || seen.has(trimmed)) continue
    seen.add(trimmed)
    result.push(trimmed)
  }
  return result
}

/**
 * 校验建群表单。
 * @returns 错误文案；通过时返回 null。
 */
export function validateGroupForm(title: string, memberIds: readonly string[]): string | null {
  const trimmedTitle = title.trim()
  if (trimmedTitle.length === 0) return '请输入群名称'
  if (trimmedTitle.length > GROUP_TITLE_MAX_LENGTH) {
    return `群名称不能超过 ${GROUP_TITLE_MAX_LENGTH} 个字符`
  }
  if (uniqueMemberIds(memberIds).length < GROUP_MEMBERS_MIN_COUNT) return '请至少选择 1 位成员'
  return null
}
