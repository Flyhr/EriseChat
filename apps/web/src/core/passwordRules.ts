// 密码规则纯函数（F-13）：登录页找回密码、设置页改密/设置密码共用同一套校验与文案。
// 与 AGENTS.md §6 / 服务端 AuthService 保持一致：8~128 字符；已有密码时须校验旧密码。

export const PASSWORD_MIN_LENGTH = 8
export const PASSWORD_MAX_LENGTH = 128

/** 校验新密码；通过返回 null，否则返回可直接展示的中文文案。 */
export function validateNewPassword(password: string): string | null {
  if (!password) {
    return '请输入新密码'
  }

  if (password.length < PASSWORD_MIN_LENGTH || password.length > PASSWORD_MAX_LENGTH) {
    return `密码长度应为 ${PASSWORD_MIN_LENGTH}~${PASSWORD_MAX_LENGTH} 个字符`
  }

  return null
}

export interface ChangePasswordFormInput {
  /** 账号是否已有密码：未设置时忽略旧密码，直接设置 */
  hasPassword: boolean
  oldPassword: string
  newPassword: string
  confirmPassword: string
}

/**
 * 改密/设置密码表单校验（按界面顺序）：
 * 新密码规则 → 两次输入一致 → 已有密码时旧密码必填。通过返回 null。
 */
export function changePasswordFormError(input: ChangePasswordFormInput): string | null {
  const passwordError = validateNewPassword(input.newPassword)
  if (passwordError) {
    return passwordError
  }

  if (input.newPassword !== input.confirmPassword) {
    return '两次输入的新密码不一致'
  }

  if (input.hasPassword && !input.oldPassword) {
    return '请输入当前密码'
  }

  return null
}
