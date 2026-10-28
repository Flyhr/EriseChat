// F-21：把用户选择的图片准备成「自定义表情」二进制（非 GIF 压缩为 128px PNG 并保留透明；GIF 原样保留动画）。
// 依赖 canvas/Image，属于浏览器侧辅助函数（不参与纯函数单测，纯校验见 customEmoji.ts）。

import { CUSTOM_EMOJI_EDGE, CUSTOM_EMOJI_TARGET_BYTES, customEmojiFileName } from './customEmoji'
import { loadImage } from './imageLoader'

export interface PreparedCustomEmoji {
  /** 实际保存/发送的二进制。 */
  blob: Blob
  /** 实际 MIME（非 GIF 统一为 image/png）。 */
  mime: string
  /** 发送时使用的文件名。 */
  fileName: string
}

/** 等比缩放到最长边 edge 并输出 PNG（保留透明通道）。 */
function renderPng(image: HTMLImageElement, edge: number): Promise<Blob | null> {
  return new Promise((resolve) => {
    const width = image.naturalWidth
    const height = image.naturalHeight
    if (width <= 0 || height <= 0) {
      resolve(null)
      return
    }
    const scale = Math.min(1, edge / Math.max(width, height))
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(width * scale))
    canvas.height = Math.max(1, Math.round(height * scale))
    const context = canvas.getContext('2d')
    if (!context) {
      resolve(null)
      return
    }
    context.clearRect(0, 0, canvas.width, canvas.height)
    context.drawImage(image, 0, 0, canvas.width, canvas.height)
    canvas.toBlob((blob) => resolve(blob), 'image/png')
  })
}

/**
 * 准备自定义表情：
 * - GIF：不压缩（保动画），文件名 emoji.gif；
 * - 其它图片：缩放到 128px PNG，体积仍超 48KB 时继续等比缩小（最低 48px）；
 * - 任何失败回退原文件（仍能添加，只是体积可能偏大）。
 */
export async function prepareCustomEmojiFile(file: File): Promise<PreparedCustomEmoji> {
  if (file.type === 'image/gif') {
    return { blob: file, mime: 'image/gif', fileName: customEmojiFileName('image/gif') }
  }

  try {
    const image = await loadImage(file)
    let edge = CUSTOM_EMOJI_EDGE
    let blob = await renderPng(image, edge)
    while (blob && blob.size > CUSTOM_EMOJI_TARGET_BYTES && edge > 48) {
      edge = Math.max(48, Math.round(edge * 0.75))
      blob = await renderPng(image, edge)
    }
    if (blob) {
      return { blob, mime: 'image/png', fileName: customEmojiFileName('image/png') }
    }
  } catch (error) {
    console.warn('[emoji] 自定义表情压缩失败，回退原图', error)
  }
  return { blob: file, mime: file.type, fileName: customEmojiFileName(file.type) }
}
