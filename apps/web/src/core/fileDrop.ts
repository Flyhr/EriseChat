// F-06 批次 1：拖拽 / 粘贴文件提取纯函数（不直接依赖 DOM 事件对象，node 环境可测）。
// 拖拽与剪贴板行为本身由 Chat.vue / SendBox.vue 接入，本文件只做数据提取、文件名兜底与批量截断。
import { extensionOf } from './attachments'

/** 一次拖拽 / 粘贴最多发送的文件数（超出部分截断并提示）。 */
export const MAX_SEND_FILES = 10

/** DragEvent.dataTransfer / ClipboardEvent.clipboardData 的最小只读结构。 */
export interface FileTransferLike {
  types?: readonly string[]
  files?: ArrayLike<unknown> | null
}

/** 批量截断结果：rejectedCount 为被丢弃的数量（用于「最多一次发送 N 个」提示）。 */
export interface TruncateFilesResult {
  files: File[]
  rejectedCount: number
}

/** 常见 MIME → 扩展名映射（与 attachments.ts 的服务端白名单对齐）。 */
const MIME_EXTENSIONS: Readonly<Record<string, string>> = {
  'image/png': 'png',
  'image/jpeg': 'jpg',
  'image/gif': 'gif',
  'image/webp': 'webp',
  'application/pdf': 'pdf',
  'text/plain': 'txt',
  'application/msword': 'doc',
  'application/vnd.openxmlformats-officedocument.wordprocessingml.document': 'docx',
  'application/vnd.ms-excel': 'xls',
  'application/vnd.openxmlformats-officedocument.spreadsheetml.sheet': 'xlsx',
  'application/vnd.openxmlformats-officedocument.presentationml.presentation': 'pptx',
  'application/zip': 'zip',
  'application/x-zip-compressed': 'zip',
  'application/x-7z-compressed': '7z',
}

/** 判断拖拽数据里是否含文件（DataTransfer.types 在拖拽过程中可用，drop 后可能为空）。 */
export function hasFiles(transfer: FileTransferLike | null | undefined): boolean {
  if (!transfer) return false
  if (transfer.files && transfer.files.length > 0) return true
  if (!transfer.types) return false
  return transfer.types.includes('Files')
}

/** 提取 dataTransfer 中的 File 列表，忽略非 File 项；无文件返回空数组。 */
export function filesOf(transfer: FileTransferLike | null | undefined): File[] {
  return collectFiles(transfer?.files)
}

/** 提取剪贴板数据中的 File 列表（粘贴截图 / 复制的文件），无文件返回空数组。 */
export function filesOfClipboard(transfer: FileTransferLike | null | undefined): File[] {
  return collectFiles(transfer?.files)
}

/**
 * 兜底被拖拽/粘贴文件的名称：系统截图粘贴等场景可能没有文件名或没有扩展名，
 * 此时按 MIME 生成可用名称（扩展名白名单校验依赖扩展名）；合法名称原样返回。
 */
export function normalizeDroppedFile(file: File, now: number = Date.now()): File {
  if (file.name && extensionOf(file.name)) return file

  const extension = MIME_EXTENSIONS[file.type] ?? 'bin'
  const prefix = file.type.startsWith('image/') ? '粘贴图片' : '粘贴文件'
  const stamp = Number.isFinite(now) ? Math.max(0, Math.floor(now)) : 0
  const name = `${prefix}-${stamp}.${extension}`
  try {
    return new File([file], name, { type: file.type, lastModified: file.lastModified })
  } catch {
    // 极端环境（File 构造不可用）退回原名，由后续白名单校验给出明确错误。
    return file
  }
}

/** 截断到一次最多 limit 个，返回保留列表与丢弃数量。 */
export function truncateFiles(files: readonly File[], limit: number = MAX_SEND_FILES): TruncateFilesResult {
  if (!Array.isArray(files)) return { files: [], rejectedCount: 0 }
  const safeLimit = Number.isFinite(limit) ? Math.max(1, Math.floor(limit)) : MAX_SEND_FILES
  const kept = files.slice(0, safeLimit)
  return { files: kept, rejectedCount: Math.max(0, files.length - kept.length) }
}

function collectFiles(source: ArrayLike<unknown> | null | undefined): File[] {
  if (!source) return []
  const result: File[] = []
  for (let index = 0; index < source.length; index += 1) {
    const item = source[index]
    if (item instanceof File) result.push(item)
  }
  return result
}
