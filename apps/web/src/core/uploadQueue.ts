// F-35：附件上传队列的纯类型与判定函数（进度/取消/单项重试）。
// 说明：真实上传由 api/files.ts 的 uploadWithProgress 完成，消息发送由 stores/messages.ts 负责；
// 这里只描述单条附件在「选择 → 上传 → 已交给消息链路」之间的本地状态，便于组件展示与单测。
import type { ReplyPreviewLike } from './replyPreview'
import type { FileContent } from '../types/frames'

/** 附件项状态：等待、上传中、发送中（上传完成、等待 ack/补拉确认）、已发送、失败（上传失败或发送失败）、已取消。 */
export type UploadItemStatus = 'pending' | 'uploading' | 'sending' | 'sent' | 'failed' | 'cancelled'

/** 单条附件上传项；conversationId/senderId 在入队时固定，切换会话不会改变归属。 */
export interface UploadItem {
  id: string
  conversationId: string
  senderId: string
  file: File
  fileName: string
  size: number
  status: UploadItemStatus
  /** 上传进度百分比（0~100），来自 XHR 的真实进度。 */
  progress: number
  /** 上传成功后服务端返回的 files.id；用于「已上传、仅消息发送失败」时避免重复上传。 */
  fileId?: string
  /** 交给消息链路时使用的 clientMsgId；重试发送时复用以保持幂等。 */
  clientMsgId?: string
  msgType?: 'image' | 'file'
  content?: FileContent
  /** C2a：图片尺寸（压缩后测量），写入 FileContent 供接收端预留占位。 */
  width?: number
  height?: number
  /** C2a：相册批次标识；同一次确认发送的多张图片共享。 */
  albumId?: string
  error?: string
  replyTo?: string
  replyPreview?: ReplyPreviewLike
}

export interface CreateUploadItemOptions {
  replyTo?: string
  replyPreview?: ReplyPreviewLike
  /** C2a：同批确认发送的多张图片共享同一相册标识。 */
  albumId?: string
}

/** C2a：多图预览项（尚未确认发送，只存在于前端内存；previewUrl 为本地 object URL）。 */
export interface PendingImageItem {
  id: string
  name: string
  /** 原始文件：确认发送时据此入上传队列。 */
  file: File
  previewUrl: string
}

/** 入队：捕获当时会话/账号/回复目标，后续不再随当前页面状态变化。 */
export function createUploadItem(
  file: File,
  conversationId: string,
  senderId: string,
  options: CreateUploadItemOptions = {},
): UploadItem {
  return {
    id: crypto.randomUUID(),
    conversationId,
    senderId,
    file,
    fileName: file.name || '未命名文件',
    size: file.size,
    status: 'pending',
    progress: 0,
    ...(options.replyTo ? { replyTo: options.replyTo } : {}),
    ...(options.replyPreview ? { replyPreview: options.replyPreview } : {}),
    ...(options.albumId ? { albumId: options.albumId } : {}),
  }
}

/** 进度归一：非有限值归零，小数四舍五入，范围夹紧到 0~100。 */
export function clampUploadProgress(value: number): number {
  if (!Number.isFinite(value)) return 0
  return Math.min(100, Math.max(0, Math.round(value)))
}

/** 状态文案（用于附件项展示；不暴露上传中模拟进度）。 */
export function uploadStatusText(
  item: Pick<UploadItem, 'status' | 'progress' | 'fileId'>,
): string {
  switch (item.status) {
    case 'pending':
      return '等待上传'
    case 'uploading':
      return `上传中 ${clampUploadProgress(item.progress)}%`
    case 'sending':
      return '发送中'
    case 'sent':
      return '已发送'
    case 'cancelled':
      return '已取消'
    case 'failed':
      // 上传完成但消息发送失败时复用原文件，提示为发送失败，避免用户以为要重新上传。
      return item.fileId ? '发送失败' : '上传失败'
  }
}

/** 上传中的附件可以取消请求。 */
export function canCancelUpload(item: Pick<UploadItem, 'status'>): boolean {
  return item.status === 'uploading'
}

/** 等待中的附件可以移出队列。 */
export function canRemoveUpload(item: Pick<UploadItem, 'status'>): boolean {
  return item.status === 'pending'
}

/** 失败项可以重试（未上传则重新上传；已上传则只重发消息）。 */
export function canRetryUpload(item: Pick<UploadItem, 'status'>): boolean {
  return item.status === 'failed'
}
