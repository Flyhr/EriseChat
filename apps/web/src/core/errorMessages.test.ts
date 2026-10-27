import { describe, expect, it } from 'vitest'
import { ApiError } from './http'
import { friendlyError } from './errorMessages'

// UI-03 批次5：统一错误文案纯函数单测（不发起网络请求）。
// 覆盖协议 §5 常用码映射、未映射回退、网络异常、未知异常与空值兜底。

function apiError(status: number, code: string, message = ''): ApiError {
  return new ApiError(status, { code, message })
}

describe('friendlyError - 错误码映射', () => {
  it('验证码相关错误码映射为中文提示', () => {
    expect(friendlyError(apiError(400, 'CODE_INVALID'))).toBe('验证码错误，请检查后重试')
    expect(friendlyError(apiError(400, 'CODE_EXPIRED'))).toBe('验证码已过期，请重新获取')
    expect(friendlyError(apiError(429, 'CODE_ATTEMPTS_EXCEEDED'))).toBe('验证码错误次数过多，请重新获取')
  })

  it('登录/账号相关错误码映射为中文提示', () => {
    expect(friendlyError(apiError(401, 'PASSWORD_INVALID'))).toBe('邮箱或密码错误')
    expect(friendlyError(apiError(423, 'ACCOUNT_LOCKED'))).toBe(
      '登录失败次数过多，账号已临时锁定，请稍后再试',
    )
    expect(friendlyError(apiError(403, 'ACCOUNT_DISABLED'))).toBe('账号已被停用，请联系管理员')
    expect(friendlyError(apiError(401, 'UNAUTHORIZED'))).toBe('登录状态已失效，请重新登录')
  })

  it('会话/权限相关错误码映射为中文提示', () => {
    expect(friendlyError(apiError(403, 'NOT_MEMBER'))).toBe('你不是该会话成员，无法执行此操作')
    expect(friendlyError(apiError(400, 'VALIDATION_FAILED'))).toBe(
      '提交的内容不符合要求，请检查后重试',
    )
    expect(friendlyError(apiError(403, 'FORBIDDEN'))).toBe('没有权限执行该操作')
  })

  it('文件与本地配置错误码映射为中文提示', () => {
    expect(friendlyError(apiError(400, 'FILE_TOO_LARGE'))).toBe('文件超过大小限制')
    expect(friendlyError(apiError(0, 'SERVER_URL_REQUIRED'))).toBe('请先填写服务器地址后重试')
  })

  it('未映射错误码回退服务端 message', () => {
    expect(friendlyError(apiError(400, 'SOME_NEW_CODE', '服务器返回的具体原因'))).toBe(
      '服务器返回的具体原因',
    )
  })

  it('未映射错误码且 message 为默认值时使用通用兜底文案', () => {
    expect(friendlyError(apiError(400, 'SOME_NEW_CODE'))).toBe('操作失败，请稍后重试')
    expect(friendlyError(apiError(500, 'SOME_NEW_CODE', '请求失败（HTTP 500）'))).toBe(
      '操作失败，请稍后重试',
    )
  })
})

describe('friendlyError - 非 ApiError 异常', () => {
  it('TypeError 按网络连接失败提示', () => {
    expect(friendlyError(new TypeError('Failed to fetch'))).toBe(
      '无法连接服务器，请检查网络或服务器地址',
    )
    expect(friendlyError(new TypeError('NetworkError when attempting to fetch resource.'))).toBe(
      '无法连接服务器，请检查网络或服务器地址',
    )
  })

  it('前端代码 TypeError 不误报为网络异常', () => {
    expect(friendlyError(new TypeError('auth.applyProfile is not a function'))).toBe(
      '操作失败，请稍后重试',
    )
  })

  it('普通 Error 使用其 message', () => {
    expect(friendlyError(new Error('本地存储写入失败'))).toBe('本地存储写入失败')
  })

  it('message 为空的 Error 使用通用兜底文案', () => {
    expect(friendlyError(new Error('   '))).toBe('操作失败，请稍后重试')
  })

  it('字符串异常去空白后使用原文', () => {
    expect(friendlyError('  自定义错误  ')).toBe('自定义错误')
  })

  it('null/undefined/空字符串等未知异常使用兜底文案', () => {
    expect(friendlyError(null)).toBe('操作失败，请稍后重试')
    expect(friendlyError(undefined)).toBe('操作失败，请稍后重试')
    expect(friendlyError('   ')).toBe('操作失败，请稍后重试')
    expect(friendlyError(42)).toBe('操作失败，请稍后重试')
  })
})
