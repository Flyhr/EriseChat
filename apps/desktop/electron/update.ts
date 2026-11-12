import { app, ipcMain, type IpcMainInvokeEvent, type WebContents } from 'electron'
import { createHash } from 'node:crypto'
import fs from 'node:fs'
import path from 'node:path'
import { autoUpdater, type UpdateInfo } from 'electron-updater'
import { getSettings } from './ipc'

// P2：Windows 应用内更新（electron-updater 6.8.12，generic provider）。
// 安全边界：更新网络请求统一在 HttpExecutor.createRequest 处按规范化 origin 校验，覆盖
// /api/version、latest.yml、整包、blockmap、Range 与每一跳重定向；校验发生在请求发出之前。
// 正式包只允许 HTTPS，未打包的调试进程才允许回环 HTTP。
// 双清单（/api/version 与 latest.yml）一致才形成候选；主进程持有候选，渲染层不传 URL、摘要或路径。

const FEED_PATH = '/download/files'
const MANIFEST_PATH = '/api/version'
const INSTALLER_NAME_PATTERN = /^EriseChat-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$/i
const SHA256_PATTERN = /^[0-9a-f]{64}$/
const LOOPBACK_HOSTNAMES = new Set(['localhost', '127.0.0.1', '::1', '[::1]'])

export interface UpdateArtifact {
  version: string
  fileName: string
  url: string
  sha256: string
}

export interface UpdateCheckResult {
  available: boolean
  currentVersion: string
  latestVersion: string
  /** 双清单一致且同源校验通过，可走应用内下载安装。 */
  canInstall: boolean
  /** canInstall=false 且确有新版本时的原因。 */
  reason?: string
  /** 同源 HTTPS 下载页；能走到这里说明 origin 已通过 HTTPS 策略。 */
  manualUrl: string
}

export type UpdateIpcResult = { ok: true } | { ok: false; message: string }
export type UpdateCheckIpcResult = { ok: true; result: UpdateCheckResult } | { ok: false; message: string }

interface VersionManifest {
  artifacts?: { desktop?: unknown } | null
}

interface UpdateCandidate {
  origin: string
  version: string
  fileName: string
  sha256: string
  /** 下载完成后由 updater 返回的实际文件路径。 */
  downloadedFile: string | null
}

/** electron-updater 传给 HttpExecutor.createRequest 的 Node 请求选项（只取校验所需字段）。 */
interface UpdaterRequestOptions {
  protocol?: string
  hostname?: string
  host?: string
  port?: string | number
  path?: string
}

interface UpdaterExecutor {
  createRequest: (options: UpdaterRequestOptions, callback: (response: unknown) => void) => unknown
}

let candidate: UpdateCandidate | null = null
let progressSender: WebContents | null = null
let networkGuarded = false
/** 最近一次被同源校验拦下的地址；用于把更新器抛出的网络错误翻译成可读原因。 */
let originViolation: string | null = null

function errorMessage(error: unknown): string {
  if (error instanceof Error && error.message) return error.message
  return '未知错误'
}

function parseVersion(value: unknown): [number, number, number] {
  if (typeof value !== 'string') return [0, 0, 0]
  const parts = value.trim().split('.')
  const numbers = [0, 1, 2].map((index) => {
    const parsed = Number.parseInt(parts[index] ?? '', 10)
    return Number.isFinite(parsed) && parsed >= 0 ? parsed : 0
  })
  return [numbers[0], numbers[1], numbers[2]]
}

/** 版本比较：a>b 返回正数，相等返回 0，a<b 返回负数；只比较 x.y.z 三段。 */
function compareVersions(a: unknown, b: unknown): number {
  const left = parseVersion(a)
  const right = parseVersion(b)
  for (let index = 0; index < 3; index += 1) {
    if (left[index] !== right[index]) return left[index] - right[index]
  }
  return 0
}

function isLoopback(url: URL): boolean {
  return url.protocol === 'http:' && LOOPBACK_HOSTNAMES.has(url.hostname)
}

/**
 * 当前允许的更新 origin（规范化后只含协议 + 主机 + 端口）。
 * 正式包只接受 HTTPS；未打包的调试进程额外允许回环 HTTP。
 */
function allowedOrigin(): string {
  const raw = getSettings().serverUrl.trim()
  if (!raw) throw new Error('请先在设置中填写服务器地址')
  const url = new URL(raw)
  if (url.protocol === 'https:') return url.origin
  if (!app.isPackaged && isLoopback(url)) return url.origin
  throw new Error('应用内更新要求 HTTPS 服务器（调试构建可用回环 HTTP）')
}

/** 把 createRequest 的选项还原成规范化 origin；无法识别时返回 null（按不允许处理）。 */
function requestOrigin(options: UpdaterRequestOptions): string | null {
  const protocol = options.protocol
  let hostname = options.hostname
  let port = options.port
  if (!hostname && options.host) {
    const separator = options.host.indexOf(':')
    hostname = separator >= 0 ? options.host.slice(0, separator) : options.host
    if (!port && separator >= 0) port = options.host.slice(separator + 1)
  }
  if (!protocol || !hostname) return null
  try {
    return new URL(`${protocol}//${hostname}${port ? `:${port}` : ''}`).origin
  } catch {
    return null
  }
}

function assertSameOrigin(options: UpdaterRequestOptions): void {
  const origin = allowedOrigin()
  const actual = requestOrigin(options)
  if (actual !== origin) {
    originViolation = actual ?? '无法识别的地址'
    throw new Error(`更新请求不在当前服务器范围内：${originViolation}`)
  }
}

/**
 * 在更新器的 HTTP 执行器上装同源闸门。createRequest 是 latest.yml、整包、blockmap、
 * Range 与重定向每一跳的唯一入口（6.8.12 已核对），在这里校验可以做到“发出前拒绝”。
 * 内部结构变化时宁可报错停用应用内更新，也不静默放行。
 */
function guardUpdaterNetwork(): void {
  if (networkGuarded) return
  const executor = (autoUpdater as unknown as { httpExecutor?: UpdaterExecutor }).httpExecutor
  if (!executor || typeof executor.createRequest !== 'function') {
    throw new Error('更新器网络层不可用，已停止应用内更新')
  }
  const createRequest = executor.createRequest.bind(executor)
  executor.createRequest = (options, callback) => {
    assertSameOrigin(options)
    return createRequest(options, callback)
  }
  networkGuarded = true
}

function updateFailureReason(error: unknown, fallback: string): string {
  if (originViolation) return `更新地址不在当前服务器范围内：${originViolation}`
  return `${fallback}：${errorMessage(error)}`
}

async function fetchManifest(origin: string): Promise<VersionManifest> {
  const url = new URL(MANIFEST_PATH, origin)
  if (url.origin !== origin) throw new Error('更新清单地址不在当前服务器范围内')
  const response = await fetch(url, { headers: { Accept: 'application/json' } })
  if (!response.ok) throw new Error(`服务器返回 ${response.status}`)
  return (await response.json()) as VersionManifest
}

/** 校验 /api/version 的桌面产物：文件名、摘要格式、同源地址与下载路由。 */
function validateArtifact(raw: unknown, origin: string): UpdateArtifact | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const version = typeof record.version === 'string' ? record.version.trim() : ''
  const fileName = typeof record.fileName === 'string' ? record.fileName.trim() : ''
  const url = typeof record.url === 'string' ? record.url.trim() : ''
  const sha256 = typeof record.sha256 === 'string' ? record.sha256.trim().toLowerCase() : ''
  if (!version || !fileName || !url || !sha256) return null
  if (path.basename(fileName) !== fileName) return null
  if (!INSTALLER_NAME_PATTERN.test(fileName)) return null
  if (!SHA256_PATTERN.test(sha256)) return null
  let resolved: URL
  try {
    resolved = new URL(url, origin)
  } catch {
    return null
  }
  if (resolved.origin !== origin) return null
  if (!resolved.pathname.startsWith(`${FEED_PATH}/`)) return null
  return { version, fileName, url: resolved.pathname, sha256 }
}

/** 双清单一致性：版本相同，且 latest.yml 中每个文件都解析到同源的同一安装包。 */
function validateUpdateInfo(info: UpdateInfo | undefined, artifact: UpdateArtifact, feedUrl: string): string | null {
  if (!info) return '更新清单不可用'
  if (info.version !== artifact.version) return '更新清单版本与服务器不一致'
  const files = info.files ?? []
  if (files.length === 0) return '更新清单缺少安装包'
  const feed = new URL(`${feedUrl}/`)
  for (const file of files) {
    let resolved: URL
    try {
      resolved = new URL(file.url, feed)
    } catch {
      return '更新清单中的安装包地址无效'
    }
    if (resolved.origin !== feed.origin) return '更新清单中的安装包地址不在当前服务器范围内'
    if (resolved.pathname !== `${FEED_PATH}/${artifact.fileName}`) {
      return '更新清单中的安装包与服务器产物不一致'
    }
  }
  return null
}

function sha256OfFile(filePath: string): Promise<string> {
  return new Promise((resolve, reject) => {
    const hash = createHash('sha256')
    const stream = fs.createReadStream(filePath)
    stream.on('data', (chunk) => hash.update(chunk))
    stream.on('error', reject)
    stream.on('end', () => resolve(hash.digest('hex')))
  })
}

async function checkForUpdate(): Promise<UpdateCheckIpcResult> {
  candidate = null
  originViolation = null
  const currentVersion = app.getVersion()
  try {
    guardUpdaterNetwork()
    const origin = allowedOrigin()
    const manifest = await fetchManifest(origin)
    const artifact = validateArtifact(manifest.artifacts?.desktop, origin)
    const latestVersion = artifact?.version ?? currentVersion
    const available = Boolean(artifact) && compareVersions(latestVersion, currentVersion) > 0
    const manualUrl = new URL('/download', origin).toString()

    if (!available || !artifact) {
      return { ok: true, result: { available: false, currentVersion, latestVersion, canInstall: false, manualUrl } }
    }
    if (!app.isPackaged) {
      return {
        ok: true,
        result: { available: true, currentVersion, latestVersion, canInstall: false, reason: '开发态不支持应用内更新', manualUrl },
      }
    }

    const feedUrl = `${origin}${FEED_PATH}`
    autoUpdater.setFeedURL({ provider: 'generic', url: feedUrl })
    let updateInfo: UpdateInfo | undefined
    try {
      const checked = await autoUpdater.checkForUpdates()
      updateInfo = checked?.updateInfo
    } catch (error) {
      return {
        ok: true,
        result: {
          available: true,
          currentVersion,
          latestVersion,
          canInstall: false,
          reason: updateFailureReason(error, '更新清单不可用'),
          manualUrl,
        },
      }
    }

    const mismatch = validateUpdateInfo(updateInfo, artifact, feedUrl)
    if (mismatch) {
      return { ok: true, result: { available: true, currentVersion, latestVersion, canInstall: false, reason: mismatch, manualUrl } }
    }

    candidate = { origin, version: artifact.version, fileName: artifact.fileName, sha256: artifact.sha256, downloadedFile: null }
    return { ok: true, result: { available: true, currentVersion, latestVersion, canInstall: true, manualUrl } }
  } catch (error) {
    return { ok: false, message: `检查更新失败：${errorMessage(error)}` }
  }
}

async function downloadUpdate(event: IpcMainInvokeEvent): Promise<UpdateIpcResult> {
  const active = candidate
  if (!active) return { ok: false, message: '没有可用的更新，请先检查更新' }
  if (!app.isPackaged) return { ok: false, message: '开发态不支持应用内更新' }
  try {
    if (active.origin !== allowedOrigin()) {
      candidate = null
      return { ok: false, message: '服务器已切换，请重新检查更新' }
    }
    originViolation = null
    progressSender = event.sender
    let files: string[]
    try {
      files = await autoUpdater.downloadUpdate()
    } finally {
      progressSender = null
    }
    if (originViolation) {
      candidate = null
      return { ok: false, message: `更新地址不在当前服务器范围内：${originViolation}` }
    }
    const downloadedFile = files[0]
    if (!downloadedFile) return { ok: false, message: '下载完成但未找到安装包，请重试' }
    active.downloadedFile = downloadedFile
    return { ok: true }
  } catch (error) {
    progressSender = null
    if (originViolation) {
      candidate = null
      return { ok: false, message: `更新地址不在当前服务器范围内：${originViolation}` }
    }
    return { ok: false, message: `下载失败：${errorMessage(error)}` }
  }
}

async function installUpdate(): Promise<UpdateIpcResult> {
  const active = candidate
  if (!active?.downloadedFile) return { ok: false, message: '未找到已下载的安装包，请重新下载' }
  try {
    if (active.origin !== allowedOrigin()) {
      candidate = null
      return { ok: false, message: '服务器已切换，请重新检查更新' }
    }
    // updater 已按 latest.yml 校验 SHA-512；安装前再按 /api/version 核对实际文件的 SHA-256。
    const actual = await sha256OfFile(active.downloadedFile)
    if (actual !== active.sha256) {
      return { ok: false, message: '安装包校验失败（SHA-256 不一致），请重新下载' }
    }
    autoUpdater.quitAndInstall(true, true)
    return { ok: true }
  } catch (error) {
    return { ok: false, message: `安装失败：${errorMessage(error)}` }
  }
}

function onDownloadProgress(progress: { transferred: number; total: number; percent: number }): void {
  const sender = progressSender
  if (!sender || sender.isDestroyed()) return
  sender.send('update:progress', {
    downloaded: progress.transferred,
    total: progress.total,
    percent: Math.max(0, Math.min(100, Math.round(progress.percent))),
  })
}

function onUpdaterError(error: Error): void {
  // 必须消费 error 事件，否则 EventEmitter 会把更新器错误抛成未捕获异常。
  console.warn('[desktop] 更新器错误', error)
}

export function registerUpdateIpcHandlers(): void {
  autoUpdater.autoDownload = false
  autoUpdater.autoInstallOnAppQuit = false
  autoUpdater.on('download-progress', onDownloadProgress)
  autoUpdater.on('error', onUpdaterError)
  ipcMain.handle('update:check', () => checkForUpdate())
  ipcMain.handle('update:download', (event) => downloadUpdate(event))
  ipcMain.handle('update:install', () => installUpdate())
}

/** 退出/热更新前移除 IPC 监听，避免重复注册。 */
export function disposeUpdateIpcHandlers(): void {
  autoUpdater.off('download-progress', onDownloadProgress)
  autoUpdater.off('error', onUpdaterError)
  ipcMain.removeHandler('update:check')
  ipcMain.removeHandler('update:download')
  ipcMain.removeHandler('update:install')
}
