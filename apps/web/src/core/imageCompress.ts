// 图片发送前压缩（M3-07，计划 §4.5）：最长边 1280px、目标 ≤300KB，使用原生 canvas，不引入依赖。
// gif 动图原样返回（压缩会丢帧）；压缩失败回退原图并 console.warn，不阻塞发送。

import { PICK_QUALITY, computeScaledSize, extensionOf } from './attachments'
import { loadImage } from './imageLoader'

export interface CompressOptions {
  /** 最长边（默认 1280px，计划 §4.5）。 */
  maxEdge?: number
  /** 目标体积（默认 300KB，计划 §4.5）。 */
  targetBytes?: number
}

function canvasToBlob(canvas: HTMLCanvasElement, quality: number): Promise<Blob | null> {
  return new Promise((resolve) => {
    canvas.toBlob((blob) => resolve(blob), 'image/jpeg', quality)
  })
}

/** 把文件名扩展名替换为 .jpg（压缩后统一为 jpeg）。 */
function toJpegName(fileName: string): string {
  const extension = extensionOf(fileName)
  const base = extension ? fileName.slice(0, -extension.length) : fileName
  return `${base || 'image'}.jpg`
}

function toJpegFile(blob: Blob, sourceName: string): File {
  return new File([blob], toJpegName(sourceName), { type: 'image/jpeg', lastModified: Date.now() })
}

/**
 * 压缩图片文件：
 * - 非图片或 gif 动图直接返回原文件；
 * - Image + canvas 等比缩放（透明底填白）→ toBlob('image/jpeg', quality)；
 * - 按 PICK_QUALITY 阶梯尝试直到 ≤targetBytes；仍超标时用最低质量结果（比原图小才采用）；
 * - 任何异常回退原图并 console.warn。
 */
export async function compressImageFile(file: File, options: CompressOptions = {}): Promise<File> {
  const maxEdge = options.maxEdge ?? 1280
  const targetBytes = options.targetBytes ?? 300 * 1024

  if (!file.type.startsWith('image/') && !isImageExtension(file.name)) {
    return file
  }
  if (extensionOf(file.name) === '.gif' || file.type === 'image/gif') {
    // 动图不压缩，保留帧。
    return file
  }

  try {
    const image = await loadImage(file)
    const { width, height } = computeScaledSize(image.naturalWidth, image.naturalHeight, maxEdge)
    if (width <= 0 || height <= 0) return file

    const canvas = document.createElement('canvas')
    canvas.width = width
    canvas.height = height
    const context = canvas.getContext('2d')
    if (!context) return file

    // JPEG 不支持透明：先铺白底，避免透明区域变黑。
    context.fillStyle = '#ffffff'
    context.fillRect(0, 0, width, height)
    context.drawImage(image, 0, 0, width, height)

    for (const quality of PICK_QUALITY) {
      const blob = await canvasToBlob(canvas, quality)
      if (!blob) continue
      if (blob.size <= targetBytes) {
        return toJpegFile(blob, file.name)
      }
    }

    // 质量阶梯用完仍超目标：最低质量结果比原图小则采用，否则回退原图。
    const lowest = await canvasToBlob(canvas, PICK_QUALITY[PICK_QUALITY.length - 1])
    if (lowest && lowest.size < file.size) {
      return toJpegFile(lowest, file.name)
    }
    return file
  } catch (error) {
    console.warn('[image] 图片压缩失败，回退原图', error)
    return file
  }
}

function isImageExtension(fileName: string): boolean {
  const extension = extensionOf(fileName)
  return ['.png', '.jpg', '.jpeg', '.gif', '.webp'].includes(extension)
}
