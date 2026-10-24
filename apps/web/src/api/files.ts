// 文件接口（对应 server/Chat.Server/Api/FileEndpoints.cs；protocol.md §7）。
// 二进制不走 WebSocket：上传走 POST /api/files，下载走 GET /api/files/{fileId}（需成员鉴权）。
import { config } from '../core/config'
import { request } from '../core/http'
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

  /** 下载地址（用于 <img>/<a>）：token 鉴权方案 M2 确定后补充（当前仅生成 URL）。 */
  downloadUrl: (fileId: string) => `${config.apiBase}/files/${fileId}`,
}
