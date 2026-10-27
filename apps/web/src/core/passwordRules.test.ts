import { describe, expect, it } from 'vitest'
import { changePasswordFormError, validateNewPassword } from './passwordRules'

describe('validateNewPassword', () => {
  it('空密码返回请输入新密码', () => {
    expect(validateNewPassword('')).toBe('请输入新密码')
  })

  it('长度边界：7 位拒绝、8 位与 128 位通过、129 位拒绝', () => {
    expect(validateNewPassword('1234567')).toContain('8~128')
    expect(validateNewPassword('12345678')).toBeNull()
    expect(validateNewPassword('a'.repeat(128))).toBeNull()
    expect(validateNewPassword('a'.repeat(129))).toContain('8~128')
  })
})

describe('changePasswordFormError', () => {
  it('已有密码且旧密码/新密码/确认均正确时通过', () => {
    expect(
      changePasswordFormError({
        hasPassword: true,
        oldPassword: 'old-password',
        newPassword: 'new-password',
        confirmPassword: 'new-password',
      }),
    ).toBeNull()
  })

  it('未设置密码时忽略旧密码直接通过', () => {
    expect(
      changePasswordFormError({
        hasPassword: false,
        oldPassword: '',
        newPassword: 'new-password',
        confirmPassword: 'new-password',
      }),
    ).toBeNull()
  })

  it('已有密码但旧密码为空时提示输入当前密码', () => {
    expect(
      changePasswordFormError({
        hasPassword: true,
        oldPassword: '',
        newPassword: 'new-password',
        confirmPassword: 'new-password',
      }),
    ).toBe('请输入当前密码')
  })

  it('两次新密码不一致时提示', () => {
    expect(
      changePasswordFormError({
        hasPassword: false,
        oldPassword: '',
        newPassword: 'new-password',
        confirmPassword: 'new-password-2',
      }),
    ).toBe('两次输入的新密码不一致')
  })

  it('新密码规则错误优先于确认不一致', () => {
    expect(
      changePasswordFormError({
        hasPassword: false,
        oldPassword: '',
        newPassword: 'short',
        confirmPassword: 'other',
      }),
    ).toContain('8~128')
  })
})
