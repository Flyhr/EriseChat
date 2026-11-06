// 文件接口（对应 server/Chat.Server/Api/FileEndpoints.cs；protocol.md §7）。
// 二进制不走 WebSocket：上传走 POST /api/files，下载走 GET /api/files/{fileId}（需成员鉴权）。
import { resolveApiBase } from '../core/config'
import { ApiError, getAccessToken, notifyUnauthorized, request, requestBlob } from '../core/http'
import type { ApiErrorBody } from '../core/http'
import { VOICE_MAX_BYTES, VOICE_MIME } from '../core/voice'
import type { FileContent } from '../types/frames'

export interface UploadFileResponse extends FileContent {
  createdAt: number
}

/** F-35：带真实进度的上传选项；signal 取消时 XHR 会立即中断。 */
export interface UploadProgressOptions {
  onProgress?: (percent: number) => void
  signal?: AbortSignal
}

/**
 * F-35：带进度的附件上传（XMLHttpRequest；fetch 无法可靠获取上传进度）。
 * 仍是 POST /api/files 同一接口与 multipart 字段，服务端无需改动。
 */
export function uploadWithProgress(
  file: File,
  options: UploadProgressOptions = {},
): Promise<UploadFileResponse> {
  return new Promise<UploadFileResponse>((resolve, reject) => {
    if (options.signal?.aborted) {
      reject(new Error('上传已取消'))
      return
    }

    const form = new FormData()
    form.append('file', file)

    const xhr = new XMLHttpRequest()
    xhr.open('POST', `${resolveApiBase()}/files`)
    xhr.timeout = 120_000
    const token = getAccessToken()
    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)

    const cleanup = (): void => {
      options.signal?.removeEventListener('abort', onAbort)
    }
    const onAbort = (): void => {
      xhr.abort()
    }
    options.signal?.addEventListener('abort', onAbort, { once: true })

    xhr.upload.onprogress = (event: ProgressEvent): void => {
      if (!event.lengthComputable || event.total <= 0) return
      const percent = Math.round((event.loaded / event.total) * 100)
      options.onProgress?.(Math.min(100, Math.max(0, percent)))
    }
    xhr.onload = (): void => {
      cleanup()
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as UploadFileResponse)
        } catch {
          reject(new Error('上传响应解析失败'))
        }
        return
      }
      if (xhr.status === 401 && getAccessToken() === token) notifyUnauthorized()
      let body: ApiErrorBody = {}
      try {
        body = xhr.responseText ? (JSON.parse(xhr.responseText) as ApiErrorBody) : {}
      } catch {
        body = {}
      }
      reject(new ApiError(xhr.status, body))
    }
    xhr.onerror = (): void => {
      cleanup()
      reject(new Error('网络异常，上传失败'))
    }
    xhr.ontimeout = (): void => {
      cleanup()
      reject(new Error('上传超时，请重试'))
    }
    xhr.onabort = (): void => {
      cleanup()
      reject(new Error('上传已取消'))
    }
    xhr.send(form)
  })
}

export const filesApi = {
  /** 上传文件（multipart/form-data）；附件选择链路用 uploadWithProgress 展示真实进度。 */
  upload: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return request<UploadFileResponse>('/files', {
      method: 'POST',
      body: form,
      timeoutMs: 120_000,
    })
  },

  /** F-35：带真实进度的上传（附件队列使用）；取消通过 AbortSignal 中断请求。 */
  uploadWithProgress,

  /** 带 JWT 下载文件内容（图片缩略图/预览/文件保存共用）；大文件用 2 分钟超时。 */
  downloadBlob: (fileId: string) => requestBlob(`/files/${fileId}`, { timeoutMs: 120_000 }),

  /**
   * 上传语音（F-28）：录制结果是 webm/opus，服务端按扩展名归一为 audio/webm。
   * 大小/类型校验在调用前完成（服务端还会按消息层再校验一次）。
   */
  uploadVoice: (blob: Blob) => {
    if (!(blob instanceof Blob) || blob.size <= 0) {
      return Promise.reject(new Error('录音为空，无法发送'))
    }
    if (blob.size > VOICE_MAX_BYTES) {
      return Promise.reject(new Error(`语音文件超过 ${VOICE_MAX_BYTES / 1024 / 1024}MB 上限`))
    }
    if (blob.type && !blob.type.startsWith(VOICE_MIME)) {
      return Promise.reject(new Error('不支持的音频格式'))
    }
    const file = new File([blob], `voice-${Date.now()}.webm`, { type: VOICE_MIME })
    return filesApi.upload(file)
  },
}
