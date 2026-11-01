// 文件接口（对应 server/Chat.Server/Api/FileEndpoints.cs；protocol.md §7）。
// 二进制不走 WebSocket：上传走 POST /api/files，下载走 GET /api/files/{fileId}（需成员鉴权）。
import { request, requestBlob } from '../core/http'
import { VOICE_MAX_BYTES, VOICE_MIME } from '../core/voice'
import type { FileContent } from '../types/frames'

export interface UploadFileResponse extends FileContent {
  createdAt: number
}

export const filesApi = {
  /** 上传文件（multipart/form-data）；大文件上传进度条 M2 再接 XHR。 */
  upload: (file: File) => {
    const form = new FormData()
    form.append('file', file)
    return request<UploadFileResponse>('/files', {
      method: 'POST',
      body: form,
      timeoutMs: 120_000,
    })
  },

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
