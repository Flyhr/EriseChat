import { describe, expect, it } from 'vitest'
import {
  VOICE_MAX_BYTES,
  VOICE_MAX_DURATION_MS,
  VOICE_MIN_DURATION_MS,
  VOICE_MIME,
  buildVoiceContent,
  clampVoiceSeekMs,
  formatDuration,
  isVoiceContent,
  isValidVoiceDuration,
  voicePreviewText,
  voiceProgressRatio,
  voiceSeekTimeText,
} from './voice'
import { mapRecorderError } from './recorder'
import type { FileContent } from '../types/frames'

describe('formatDuration', () => {
  it('把毫秒格式化为 分:秒（不足 1 秒按 0 秒显示）', () => {
    expect(formatDuration(0)).toBe('0:00')
    expect(formatDuration(999)).toBe('0:00')
    expect(formatDuration(7000)).toBe('0:07')
    expect(formatDuration(59999)).toBe('0:59')
    expect(formatDuration(60000)).toBe('1:00')
  })

  it('非法时长回退 0:00，不抛错', () => {
    expect(formatDuration(Number.NaN)).toBe('0:00')
    expect(formatDuration(-100)).toBe('0:00')
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBe('0:00')
  })
})

describe('isValidVoiceDuration', () => {
  it('边界内合法（含 1000 / 60000）', () => {
    expect(VOICE_MIN_DURATION_MS).toBe(1000)
    expect(VOICE_MAX_DURATION_MS).toBe(60000)
    expect(isValidVoiceDuration(1000)).toBe(true)
    expect(isValidVoiceDuration(59999)).toBe(true)
    expect(isValidVoiceDuration(60000)).toBe(true)
  })

  it('越界/非整数/NaN 不合法', () => {
    expect(isValidVoiceDuration(0)).toBe(false)
    expect(isValidVoiceDuration(999)).toBe(false)
    expect(isValidVoiceDuration(60001)).toBe(false)
    expect(isValidVoiceDuration(1200.5)).toBe(false)
    expect(isValidVoiceDuration(Number.NaN)).toBe(false)
  })
})

describe('buildVoiceContent', () => {
  it('合法参数返回归一化 content（fileId 去首尾空白）', () => {
    expect(buildVoiceContent('  file-1  ', 1500)).toEqual({ fileId: 'file-1', durationMs: 1500 })
  })

  it('fileId 为空或时长非法返回 null', () => {
    expect(buildVoiceContent('', 1500)).toBeNull()
    expect(buildVoiceContent('   ', 1500)).toBeNull()
    expect(buildVoiceContent('file-1', 500)).toBeNull()
    expect(buildVoiceContent('file-1', 60001)).toBeNull()
  })
})

describe('voicePreviewText', () => {
  it('生成 [语音] 分:秒 占位文案', () => {
    expect(voicePreviewText(7000)).toBe('[语音] 0:07')
    expect(voicePreviewText(0)).toBe('[语音] 0:00')
  })
})

describe('isVoiceContent', () => {
  it('识别语音对象', () => {
    expect(isVoiceContent({ fileId: 'f1', durationMs: 1200 })).toBe(true)
  })

  it('不会把 FileContent / 字符串 / null 误判为语音', () => {
    const file: FileContent = { fileId: 'f1', name: 'a.png', size: 10, mime: 'image/png' }
    expect(isVoiceContent(file)).toBe(false)
    expect(isVoiceContent('text')).toBe(false)
    expect(isVoiceContent(null)).toBe(false)
    expect(isVoiceContent(undefined)).toBe(false)
  })
})

describe('语音常量', () => {
  it('与服务端约定一致', () => {
    expect(VOICE_MIME).toBe('audio/webm')
    expect(VOICE_MAX_BYTES).toBe(5 * 1024 * 1024)
  })
})

describe('mapRecorderError', () => {
  it('权限被拒 → 未授权使用麦克风', () => {
    expect(mapRecorderError(new DOMException('denied', 'NotAllowedError'))).toBe(
      '未授权使用麦克风',
    )
    expect(mapRecorderError({ name: 'SecurityError' })).toBe('未授权使用麦克风')
  })

  it('没有设备 / 设备被占用 → 对应提示', () => {
    expect(mapRecorderError({ name: 'NotFoundError' })).toBe('未找到麦克风设备')
    expect(mapRecorderError({ name: 'NotReadableError' })).toBe('麦克风被占用，无法开始录音')
  })

  it('未知错误回退通用文案', () => {
    expect(mapRecorderError(new Error('boom'))).toBe('无法开始录音')
    expect(mapRecorderError(null)).toBe('无法开始录音')
  })
})

describe('clampVoiceSeekMs', () => {
  it('将定位毫秒夹到 0~总时长', () => {
    expect(clampVoiceSeekMs(0, 20_000)).toBe(0)
    expect(clampVoiceSeekMs(11_000, 20_000)).toBe(11_000)
    expect(clampVoiceSeekMs(20_000, 20_000)).toBe(20_000)
  })

  it('负值/NaN/超上限/小数按整数处理', () => {
    expect(clampVoiceSeekMs(-1, 20_000)).toBe(0)
    expect(clampVoiceSeekMs(Number.NaN, 20_000)).toBe(0)
    expect(clampVoiceSeekMs(99_999, 20_000)).toBe(20_000)
    expect(clampVoiceSeekMs(1_234.9, 20_000)).toBe(1_234)
  })

  it('时长非法时回退 0', () => {
    expect(clampVoiceSeekMs(5_000, 0)).toBe(0)
    expect(clampVoiceSeekMs(5_000, -1)).toBe(0)
    expect(clampVoiceSeekMs(5_000, Number.NaN)).toBe(0)
  })
})

describe('voiceProgressRatio', () => {
  it('返回 0~1 的进度比例', () => {
    expect(voiceProgressRatio(0, 20_000)).toBe(0)
    expect(voiceProgressRatio(10_000, 20_000)).toBe(0.5)
    expect(voiceProgressRatio(20_000, 20_000)).toBe(1)
  })

  it('越界/时长非法时收敛到 0~1 或 0', () => {
    expect(voiceProgressRatio(99_999, 20_000)).toBe(1)
    expect(voiceProgressRatio(-5, 20_000)).toBe(0)
    expect(voiceProgressRatio(5_000, 0)).toBe(0)
    expect(voiceProgressRatio(5_000, Number.NaN)).toBe(0)
  })
})

describe('voiceSeekTimeText', () => {
  it('生成「当前 / 总时长」文案', () => {
    expect(voiceSeekTimeText(11_000, 20_000)).toBe('0:11 / 0:20')
    expect(voiceSeekTimeText(0, 20_000)).toBe('0:00 / 0:20')
    expect(voiceSeekTimeText(20_000, 20_000)).toBe('0:20 / 0:20')
  })

  it('非法值按 0 处理', () => {
    expect(voiceSeekTimeText(Number.NaN, 20_000)).toBe('0:00 / 0:20')
    expect(voiceSeekTimeText(-1, 20_000)).toBe('0:00 / 0:20')
    expect(voiceSeekTimeText(5_000, Number.NaN)).toBe('0:00 / 0:00')
  })
})
