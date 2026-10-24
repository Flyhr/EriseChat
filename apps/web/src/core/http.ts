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

// JWT 由 auth store 注入，避免 core 依赖 stores；M1 登录成功后调用 setAccessTokenProvider。
let accessTokenProvider: () => string | null = () => null

export function setAccessTokenProvider(provider: () => string | null): void {
  accessTokenProvider = provider
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
