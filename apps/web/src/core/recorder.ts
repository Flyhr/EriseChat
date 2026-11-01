// F-28 语音录制（浏览器 MediaRecorder）：只负责拿到 webm/opus 音频与时长。
// 纯映射/常量单独导出便于 Vitest 单测；真正调用 getUserMedia 的入口由 UI 按需调用。
import { VOICE_MAX_DURATION_MS, VOICE_MIME } from './voice'

/** 一次录音的产出（时长由客户端测量，服务端只做范围校验）。 */
export interface VoiceRecording {
  blob: Blob
  durationMs: number
}

/** 录音句柄：stop 幂等（重复调用返回同一结果），cancel 丢弃且不产生消息。 */
export interface VoiceRecorderHandle {
  /** 停止并返回录音结果；已自动停止/已停止时返回同一结果。 */
  stop: () => Promise<VoiceRecording>
  /** 取消录音并释放麦克风（停止后不再使用结果）。 */
  cancel: () => void
  /** 是否因达到 60 秒上限自动停止。 */
  readonly autoStopped: boolean
}

/** 录音错误 → 用户可读中文（错误名来自 DOMException）。 */
export function mapRecorderError(error: unknown): string {
  const name =
    typeof error === 'object' && error !== null && 'name' in error
      ? String((error as { name?: unknown }).name ?? '')
      : ''
  switch (name) {
    case 'NotAllowedError':
    case 'SecurityError':
      return '未授权使用麦克风'
    case 'NotFoundError':
    case 'DevicesNotFoundError':
      return '未找到麦克风设备'
    case 'NotReadableError':
      return '麦克风被占用，无法开始录音'
    default:
      return '无法开始录音'
  }
}

/** 优先 opus，其次容器默认；不支持的浏览器回退 audio/webm。 */
function pickMimeType(): string {
  if (typeof MediaRecorder === 'undefined') return VOICE_MIME
  const candidates = ['audio/webm;codecs=opus', VOICE_MIME]
  return candidates.find((type) => MediaRecorder.isTypeSupported(type)) ?? VOICE_MIME
}

/**
 * 开始录音：申请麦克风并返回句柄。
 * 时长上限 60 秒，达到上限自动停止（句柄 autoStopped 置 true）；错误抛中文 Error。
 */
export async function startVoiceRecording(): Promise<VoiceRecorderHandle> {
  if (
    typeof navigator === 'undefined' ||
    typeof navigator.mediaDevices?.getUserMedia !== 'function' ||
    typeof MediaRecorder === 'undefined'
  ) {
    throw new Error('当前浏览器不支持录音')
  }

  let stream: MediaStream
  try {
    stream = await navigator.mediaDevices.getUserMedia({ audio: true })
  } catch (error) {
    throw new Error(mapRecorderError(error))
  }

  const mimeType = pickMimeType()
  const recorder = new MediaRecorder(stream, { mimeType })
  const chunks: Blob[] = []
  const startedAt = Date.now()
  let autoStopped = false

  const releaseStream = (): void => {
    for (const track of stream.getTracks()) track.stop()
  }

  const result = new Promise<VoiceRecording>((resolve) => {
    recorder.ondataavailable = (event: BlobEvent) => {
      if (event.data.size > 0) chunks.push(event.data)
    }
    recorder.onstop = () => {
      releaseStream()
      const elapsed = Date.now() - startedAt
      const durationMs = Math.min(VOICE_MAX_DURATION_MS, Math.max(0, Math.round(elapsed)))
      resolve({
        blob: new Blob(chunks, { type: mimeType }),
        durationMs,
      })
    }
  })

  const maxTimer = setTimeout(() => {
    autoStopped = true
    if (recorder.state !== 'inactive') recorder.stop()
  }, VOICE_MAX_DURATION_MS)

  const handle: VoiceRecorderHandle = {
    get autoStopped() {
      return autoStopped
    },
    stop: () => {
      clearTimeout(maxTimer)
      if (recorder.state !== 'inactive') recorder.stop()
      return result
    },
    cancel: () => {
      clearTimeout(maxTimer)
      if (recorder.state !== 'inactive') recorder.stop()
    },
  }

  recorder.start()
  return handle
}
