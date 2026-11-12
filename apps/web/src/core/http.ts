// REST 封装：统一 baseURL、JWT 注入、超时、错误码映射（docs/project-overview.md §9）。
// 约定：REST 错误响应体为 { code, message, traceId }（protocol.md §5），code 与协议错误码共用枚举。

import { config, resolveApiBase } from './config'

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
  /**
   * 是否携带登录令牌并参与 401 统一注销，默认 true。
   * 匿名接口（如更新清单 /version）传 false：不附带令牌，401 也不得清登录态。
   */
  auth?: boolean
}

// JWT 由 auth store 注入，避免 core 依赖 stores；登录成功后由 auth store 调用 setAccessTokenProvider（M2 已接入）。
let accessTokenProvider: () => string | null = () => null

export function setAccessTokenProvider(provider: () => string | null): void {
  accessTokenProvider = provider
}

/** F-35：XHR 上传等非 fetch 链路读取当前令牌，避免复制鉴权逻辑。 */
export function getAccessToken(): string | null {
  return accessTokenProvider()
}

/** F-35：非 fetch 链路收到 401 时触发统一的登录态清理（上传接口不属于 /auth/*）。 */
export function notifyUnauthorized(): void {
  unauthorizedHandler?.()
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
  // M4-01：运行时服务器地址优先（设置页/登录页可改），否则回退 config.apiBase。
  const base = path.startsWith('http') ? path : `${resolveApiBase()}${path}`
  if (!query) return base
  const params = new URLSearchParams()
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null) params.set(key, String(value))
  }
  const qs = params.toString()
  return qs ? `${base}?${qs}` : base
}

/** 桌面/APK 打包态（file://）下未配置服务器地址时，相对 URL 会落到 file:///api/...，提前给出可读提示。 */
function assertReachableUrl(url: string): void {
  if (typeof window === 'undefined') return
  if (window.location.protocol !== 'file:') return
  if (/^https?:\/\//i.test(url)) return
  throw new ApiError(0, {
    code: 'SERVER_URL_REQUIRED',
    message: '请先填写服务器地址（如 http://127.0.0.1:6580），再登录或发送验证码',
  })
}

/** 读取非 2xx 错误体（空体/非 JSON 回退空对象，ApiError 使用默认文案）。 */
async function readErrorBody(response: Response): Promise<ApiErrorBody> {
  try {
    const text = await response.text()
    return text ? (JSON.parse(text) as ApiErrorBody) : {}
  } catch {
    return {}
  }
}

/** 统一发送 REST 请求：鉴权头、超时、JSON/FormData 编码、401 回调与错误体解析；成功返回原始 Response。 */
async function send(path: string, options: RequestOptions, acceptJson: boolean): Promise<Response> {
  const {
    method = 'GET',
    body,
    query,
    headers = {},
    timeoutMs = config.requestTimeoutMs,
    signal,
    auth = true,
  } = options

  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  const onExternalAbort = (): void => controller.abort()
  signal?.addEventListener('abort', onExternalAbort, { once: true })

  const finalHeaders: Record<string, string> = acceptJson
    ? { Accept: 'application/json', ...headers }
    : { ...headers }
  const token = auth ? accessTokenProvider() : null
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

  const url = buildUrl(path, query)
  assertReachableUrl(url)

  try {
    const response = await fetch(url, {
      method,
      headers: finalHeaders,
      body: payload,
      signal: controller.signal,
    })

    if (!response.ok) {
      // 401：token 失效/被撤销 → 清登录态并回登录页（认证接口自身 401 只抛错给页面提示）。
      // 旧请求的 401 不能清掉新登录态：只有 Bearer token 仍是当前 token 时才退出登录。
      if (
        response.status === 401 &&
        auth &&
        unauthorizedHandler !== null &&
        !isAuthEndpoint(path) &&
        getAccessToken() === token
      ) {
        unauthorizedHandler()
      }
      throw new ApiError(response.status, await readErrorBody(response))
    }
    return response
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

/** 发送 REST 请求并解析 JSON；非 2xx 抛 ApiError（204 返回 undefined）。 */
export async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const response = await send(path, options, true)
  if (response.status === 204) return undefined as T
  const text = await response.text()
  return (text ? JSON.parse(text) : undefined) as T
}

/** 下载二进制内容（文件/图片）：成功返回 Blob；非 2xx 抛 ApiError（错误体解析与 request 一致）。 */
export async function requestBlob(path: string, options: RequestOptions = {}): Promise<Blob> {
  const response = await send(path, options, false)
  return await response.blob()
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
