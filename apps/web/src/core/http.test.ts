// REST 封装单测（M2-07）：ApiError 错误体映射、查询串、JWT 注入、401 统一处理。
// 通过 stub 全局 fetch 避免真实网络，也不依赖 DOM。
import { afterEach, describe, expect, it, vi } from 'vitest'
import { ApiError, http, request, setAccessTokenProvider, setUnauthorizedHandler } from './http'

interface FetchCall {
  input: RequestInfo | URL
  init?: RequestInit
}

function jsonResponse(status: number, body: unknown): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { 'Content-Type': 'application/json' },
  })
}

/** 用自定义响应替换全局 fetch，并记录调用参数（避免依赖 mock.calls 的元组类型）。 */
function stubFetch(handler: (call: FetchCall) => Response): FetchCall[] {
  const calls: FetchCall[] = []
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const call: FetchCall = { input, init }
      calls.push(call)
      return handler(call)
    }),
  )
  return calls
}

afterEach(() => {
  vi.unstubAllGlobals()
  vi.restoreAllMocks()
  setAccessTokenProvider(() => null)
  setUnauthorizedHandler(null)
})

describe('ApiError', () => {
  it('透传 status/code/message/traceId', () => {
    const error = new ApiError(409, {
      code: 'EMAIL_ALREADY_REGISTERED',
      message: '该邮箱已注册',
      traceId: 'trace-1',
    })
    expect(error).toBeInstanceOf(Error)
    expect(error.name).toBe('ApiError')
    expect(error.status).toBe(409)
    expect(error.code).toBe('EMAIL_ALREADY_REGISTERED')
    expect(error.message).toBe('该邮箱已注册')
    expect(error.traceId).toBe('trace-1')
  })

  it('缺少 code/message 时使用默认值', () => {
    const error = new ApiError(500, {})
    expect(error.code).toBe('UNKNOWN')
    expect(error.message).toBe('请求失败（HTTP 500）')
    expect(error.traceId).toBeUndefined()
  })
})

describe('request 错误映射', () => {
  it('非 2xx 且响应体为统一错误体时抛 ApiError', async () => {
    stubFetch(() => jsonResponse(400, { code: 'VALIDATION_FAILED', message: '参数错误', traceId: 't-2' }))
    await expect(request('/auth/login/code', { method: 'POST', body: { email: 'a@b.c' } })).rejects.toMatchObject({
      name: 'ApiError',
      status: 400,
      code: 'VALIDATION_FAILED',
      traceId: 't-2',
    })
  })

  it('204 无内容时返回 undefined', async () => {
    stubFetch(() => new Response(null, { status: 204 }))
    await expect(http.delete('/auth/logout')).resolves.toBeUndefined()
  })

  it('2xx 时解析 JSON 响应体', async () => {
    stubFetch(() => jsonResponse(200, { status: 'ok' }))
    await expect(http.get<{ status: string }>('/health')).resolves.toEqual({ status: 'ok' })
  })

  it('query 只拼接有值的参数', async () => {
    const calls = stubFetch(() => jsonResponse(200, []))
    await http.get('/conversations', { query: { beforeSeq: 42, limit: 50, empty: undefined, none: null } })
    expect(String(calls[0]?.input)).toBe('/api/conversations?beforeSeq=42&limit=50')
  })

  it('访问令牌通过 Authorization 头注入', async () => {
    const calls = stubFetch(() => jsonResponse(200, {}))
    setAccessTokenProvider(() => 'token-abc')
    await http.get('/me')
    const headers = calls[0]?.init?.headers as Record<string, string>
    expect(headers.Authorization).toBe('Bearer token-abc')
  })
})

describe('401 统一处理', () => {
  it('非认证接口的 401 触发回调并抛 ApiError', async () => {
    const handler = vi.fn()
    setUnauthorizedHandler(handler)
    stubFetch(() => jsonResponse(401, { code: 'UNAUTHORIZED', message: '未登录' }))
    await expect(http.get('/me')).rejects.toBeInstanceOf(ApiError)
    expect(handler).toHaveBeenCalledTimes(1)
  })

  it('认证接口自身的 401 只抛错、不触发回调', async () => {
    const handler = vi.fn()
    setUnauthorizedHandler(handler)
    stubFetch(() => jsonResponse(401, { code: 'PASSWORD_INVALID', message: '邮箱或密码错误' }))
    await expect(http.post('/auth/login/password', {})).rejects.toMatchObject({ code: 'PASSWORD_INVALID' })
    expect(handler).not.toHaveBeenCalled()
  })
})
