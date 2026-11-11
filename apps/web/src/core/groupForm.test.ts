import { describe, expect, it } from 'vitest'
import {
  GROUP_MEMBERS_MIN_COUNT,
  GROUP_TITLE_MAX_LENGTH,
  uniqueMemberIds,
  validateGroupForm,
} from './groupForm'

// UI-03 批次5：建群表单校验纯函数单测（与后端 title 1~50、成员去重后 ≥1 的规则一致）。

describe('uniqueMemberIds', () => {
  it('去重并保持首次出现顺序', () => {
    expect(uniqueMemberIds(['u-2', 'u-1', 'u-2', 'u-3', 'u-1'])).toEqual(['u-2', 'u-1', 'u-3'])
  })

  it('忽略空字符串与纯空白项', () => {
    expect(uniqueMemberIds(['', 'u-1', '   ', 'u-1'])).toEqual(['u-1'])
  })

  it('去除首尾空白后再去重', () => {
    expect(uniqueMemberIds([' u-1 ', 'u-1', 'u-2 '])).toEqual(['u-1', 'u-2'])
  })
})

describe('validateGroupForm', () => {
  it('群名称为空或纯空白时提示输入群名称', () => {
    expect(validateGroupForm('', ['u-1'])).toBe('请输入群名称')
    expect(validateGroupForm('   ', ['u-1'])).toBe('请输入群名称')
  })

  it('群名称为 1 与 MAX 边界时通过，超长时提示', () => {
    expect(validateGroupForm('a', ['u-1'])).toBeNull()
    expect(validateGroupForm('群'.repeat(GROUP_TITLE_MAX_LENGTH), ['u-1'])).toBeNull()
    expect(validateGroupForm('群'.repeat(GROUP_TITLE_MAX_LENGTH + 1), ['u-1'])).toBe(
      `群名称不能超过 ${GROUP_TITLE_MAX_LENGTH} 个字符`,
    )
  })

  it('按去首尾空白后的长度校验群名称', () => {
    const padded = `  ${'群'.repeat(GROUP_TITLE_MAX_LENGTH)}  `
    expect(validateGroupForm(padded, ['u-1'])).toBeNull()
  })

  it('未选择成员时提示至少选择 1 位成员', () => {
    expect(validateGroupForm('项目群', [])).toBe('请至少选择 1 位成员')
    expect(validateGroupForm('项目群', ['', '   '])).toBe('请至少选择 1 位成员')
    expect(GROUP_MEMBERS_MIN_COUNT).toBe(1)
  })

  it('名称与成员均合法时通过', () => {
    expect(validateGroupForm('项目群', ['u-1', 'u-2'])).toBeNull()
  })

  it('成员候选只有重复项时仍视为已选成员', () => {
    expect(validateGroupForm('项目群', ['u-1', 'u-1'])).toBeNull()
  })
})
