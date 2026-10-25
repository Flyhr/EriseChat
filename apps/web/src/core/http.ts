// REST 封装：统一 baseURL、JWT 注入、超时、错误码映射（docs/project-overview.md §9）。
// 约定：REST 错误响应体为 { code, message, traceId }（protocol.md §5），code 与协议错误码共用枚举。

import { config } from './config'

export interface ApiErrorBody {
  code?: string
  message?: string
  traceId?: string
}

export class ApiError extends Error {
  readonly status: number
  readonly code: string
  readonly traceId?: string

  constructor(status: number, body: ApiErrorBody) {
    super(body.message || `请求失败（HTTP ${status}）`)
    this.name = 'ApiError'
    this.status = status
    this.code = body.code ?? 'UNKNOWN'
    this.traceId = body.traceId
  }
}

export type QueryValue = string | number | boolean | undefined | null

export interface RequestOptions {
  method?: 'GET' | 'POST' | 'PUT' | 'PATCH' | 'DELETE'
  body?: unknown
  query?: Record<string, QueryValue>
  headers?: Record<string, string>
  timeoutMs?: number
  signal?: AbortSignal
}

// JWT 由 auth store 注入，避免 core 依赖 stores；登录成功后由 auth store 调用 setAccessTokenProvider（M2 已接入）。
let accessTokenProvider: () => string | null = () => null

export function setAccessTokenProvider(provider: () => string | null): void {
  accessTokenProvider = provider
}

// 401 统一处理（M2-02）：core 不反向依赖 store/router，由应用入口注册回调（project-overview.md §10）。
// 认证接口自身返回 401（如密码错误）不触发回调，避免 /login ↔ /login 循环跳转。
let unauthorizedHandler: (() => void) | null = null

export function setUnauthorizedHandler(handler: (() => void) | null): void {
  unauthorizedHandler = handler
}

/** 认证类接口（/auth/*）：其 401 属业务错误，由页面提示，不清登录态。 */
function isAuthEndpoint(path: string): boolean {
  return path.startsWith('/auth/')
}

function buildUrl(path: string, query?: Record<string, QueryValue>): string {
  const base = path.startsWith('http') ? path : `${config.apiBase}${path}`
  if (!query) return base
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null) params.set(key, String(value))
  }
  const qs = params.toString()
  return qs ? `${base}?${qs}` : base
}

/** 发送 REST 请求并解析 JSON；非 2xx 抛 ApiError（204 返回 undefined）。 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const {
    method = 'GET',
    body,
    query,
    headers = {},
    timeoutMs = config.requestTimeoutMs,
    signal,
  } = options

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  const onExternalAbort = (): void => controller.abort()
  signal?.addEventListener('abort', onExternalAbort, { once: true })

  const finalHeaders: Record<string, string> = { Accept: 'application/json', ...headers }
  const token = accessTokenProvider()
  if (token) finalHeaders.Authorization = `Bearer ${token}`

  let payload: BodyInit | undefined
  if (body !== undefined) {
    if (body instanceof FormData) {
      // 交给浏览器设置 multipart boundary，不要手写 Content-Type。
      payload = body
    } else {
      finalHeaders['Content-Type'] = 'application/json'
      payload = JSON.stringify(body)
    }
  }

  try {
    const response = await fetch(buildUrl(path, query), {
      method,
      headers: finalHeaders,
      body: payload,
      signal: controller.signal,
    })

    if (response.status === 204) return undefined as T

    const text = await response.text()
    const data: unknown = text ? JSON.parse(text) : undefined

    if (!response.ok) {
      // 401：token 失效/被撤销 → 清登录态并回登录页（认证接口自身 401 只抛错给页面提示）。
      if (response.status === 401 && unauthorizedHandler !== null && !isAuthEndpoint(path)) {
        unauthorizedHandler()
      }
      throw new ApiError(response.status, (data ?? {}) as ApiErrorBody)
    }
    return data as T
  } catch (error) {
    if (error instanceof ApiError) throw error
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('请求超时或已取消')
    }
    throw error instanceof Error ? error : new Error(String(error))
  } finally {
    clearTimeout(timer)
    signal?.removeEventListener('abort', onExternalAbort)
  }
}

/**
 * 下载二进制内容（M3-07 文件/图片）：复用鉴权头、超时与 401 回调；
 * 非 2xx 时按 text 解析 JSON 错误体（{code,message,traceId}）并抛 ApiError。
 * 与 request 的区别：成功响应返回 Blob 而不是解析 JSON。
 */
export async function requestBlob(path: string, options: RequestOptions = {}): Promise<Blob> {
  const {
    method = 'GET',
    body,
    query,
    headers = {},
    timeoutMs = config.requestTimeoutMs,
    signal,
  } = options

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  const onExternalAbort = (): void => controller.abort()
  signal?.addEventListener('abort', onExternalAbort, { once: true })

  const finalHeaders: Record<string, string> = { ...headers }
  const token = accessTokenProvider()
  if (token) finalHeaders.Authorization = `Bearer ${token}`

  let payload: BodyInit | undefined
  if (body !== undefined) {
    if (body instanceof FormData) {
      payload = body
    } else {
      finalHeaders['Content-Type'] = 'application/json'
      payload = JSON.stringify(body)
    }
  }

  try {
    const response = await fetch(buildUrl(path, query), {
      method,
      headers: finalHeaders,
      body: payload,
      signal: controller.signal,
    })

    if (!response.ok) {
      if (response.status === 401 && unauthorizedHandler !== null && !isAuthEndpoint(path)) {
        unauthorizedHandler()
      }
      let errorBody: ApiErrorBody = {}
      try {
        const text = await response.text()
        if (text) errorBody = JSON.parse(text) as ApiErrorBody
      } catch {
        // 非 JSON 错误体（如网关页）：保留空错误体，ApiError 使用默认文案。
      }
      throw new ApiError(response.status, errorBody)
    }

    return await response.blob()
  } catch (error) {
    if (error instanceof ApiError) throw error
    if (error instanceof DOMException && error.name === 'AbortError') {
      throw new Error('请求超时或已取消')
    }
    throw error instanceof Error ? error : new Error(String(error))
  } finally {
    clearTimeout(timer)
    signal?.removeEventListener('abort', onExternalAbort)
  }
}

export const http = {
  get: <T>(path: string, options?: Omit<RequestOptions, 'method' | 'body'>): Promise<T> =>
    request<T>(path, { ...options, method: 'GET' }),

  post: <T>(path: string, body?: unknown, options?: Omit<RequestOptions, 'method' | 'body'>): Promise<T> =>
    request<T>(path, { ...options, method: 'POST', body }),

  put: <T>(path: string, body?: unknown, options?: Omit<RequestOptions, 'method' | 'body'>): Promise<T> =>
    request<T>(path, { ...options, method: 'PUT', body }),

  patch: <T>(path: string, body?: unknown, options?: Omit<RequestOptions, 'method' | 'body'>): Promise<T> =>
    request<T>(path, { ...options, method: 'PATCH', body }),

  delete: <T>(path: string, options?: Omit<RequestOptions, 'method' | 'body'>): Promise<T> =>
    request<T>(path, { ...options, method: 'DELETE' }),
}
