// 附件选择校验与展示辅助（M3-07，纯函数，便于 Vitest 单测）。
// 白名单与上限必须与服务端 server/Chat.Server/Modules/Files/FileService.cs 保持一致：
// 12 种扩展名（png/jpg/jpeg/gif/webp/pdf/txt/docx/xlsx/pptx/zip/7z），默认上限 50MB（AppOptions.UploadMaxBytes）。

/** 上传大小上限（与服务端 AppOptions.UploadMaxBytes 默认值一致）。 */
export const MAX_UPLOAD_BYTES = 50 * 1024 * 1024

/** 允许上传的扩展名（小写，含点；与服务端 AllowedExtensions 一致）。 */
export const ALLOWED_EXTENSIONS = [
  '.png',
  '.jpg',
  '.jpeg',
  '.gif',
  '.webp',
  '.pdf',
  '.txt',
  '.docx',
  '.xlsx',
  '.pptx',
  '.zip',
  '.7z',
] as const

/** 可按图片渲染/压缩的扩展名（动图 gif 保留原文件）。 */
const IMAGE_EXTENSIONS = new Set<string>(['.png', '.jpg', '.jpeg', '.gif', '.webp'])

/** 图片发送前的压缩质量阶梯（从高到低尝试，直到体积达标）。 */
export const PICK_QUALITY = [0.85, 0.7, 0.55, 0.4, 0.3] as const

export type AttachmentValidation = { ok: true } | { ok: false; reason: string }

/** 取文件名扩展名（小写、含点）；无扩展名返回空串。 */
export function extensionOf(fileName: string): string {
  const dot = fileName.lastIndexOf('.')
  return dot >= 0 ? fileName.slice(dot).toLowerCase() : ''
}

/** 是否为图片类附件（png/jpg/jpeg/gif/webp）。 */
export function isImageFile(file: File): boolean {
  return IMAGE_EXTENSIONS.has(extensionOf(file.name))
}

/** 发送前校验附件：类型白名单、空文件、超过 50MB。返回可展示的中文原因，不通过时不产生消息。 */
export function validateAttachment(file: File): AttachmentValidation {
  const extension = extensionOf(file.name)
  if (!(ALLOWED_EXTENSIONS as readonly string[]).includes(extension)) {
    return { ok: false, reason: `不支持的文件类型${extension ? `（${extension}）` : '（无扩展名）'}` }
  }
  if (file.size <= 0) {
    return { ok: false, reason: '文件为空，不能发送' }
  }
  if (file.size > MAX_UPLOAD_BYTES) {
    return {
      ok: false,
      reason: `文件超过 ${Math.floor(MAX_UPLOAD_BYTES / 1024 / 1024)}MB 上限`,
    }
  }
  return { ok: true }
}

/** 把字节数格式化为展示文案：0/1023 用 B，1KB~1MB 用 KB，≥1MB 用 MB（保留 1 位小数）。 */
export function formatFileSize(bytes: number): string {
  if (!Number.isFinite(bytes) || bytes <= 0) return '0 B'
  if (bytes < 1024) return `${Math.round(bytes)} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

/**
 * 计算等比缩放后的尺寸：最长边不超过 maxEdge；小图不放大；保证至少 1px。
 */
export function computeScaledSize(
  width: number,
  height: number,
  maxEdge = 1280,
): { width: number; height: number } {
  if (!Number.isFinite(width) || !Number.isFinite(height) || width <= 0 || height <= 0) {
    return { width: 0, height: 0 }
  }
  const longest = Math.max(width, height)
  if (longest <= maxEdge) {
    return { width: Math.round(width), height: Math.round(height) }
  }
  const scale = maxEdge / longest
  return {
    width: Math.max(1, Math.round(width * scale)),
    height: Math.max(1, Math.round(height * scale)),
  }
}
