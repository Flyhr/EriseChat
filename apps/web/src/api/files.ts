// 文件接口（对应 server/Chat.Server/Api/FileEndpoints.cs；protocol.md §7）。
// 二进制不走 WebSocket：上传走 POST /api/files，下载走 GET /api/files/{fileId}（需成员鉴权）。
import { resolveApiBase } from '../core/config'
import { request, requestBlob } from '../core/http'
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

  /**
   * 仅调试用：直接拼 URL 不带 Authorization（服务端要求 JWT 鉴权，裸 URL 会 401）。
   * 正式下载必须走 downloadBlob（M3-07）。
   */
  downloadUrl: (fileId: string) => `${resolveApiBase()}/files/${fileId}`,

  /** 带 JWT 下载文件内容（图片缩略图/预览/文件保存共用）；大文件用 2 分钟超时。 */
  downloadBlob: (fileId: string) => requestBlob(`/files/${fileId}`, { timeoutMs: 120_000 }),
}
