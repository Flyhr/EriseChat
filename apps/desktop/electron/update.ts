import { app, ipcMain, type IpcMainInvokeEvent } from 'electron'
import { spawn } from 'node:child_process'
import { createHash } from 'node:crypto'
import { once } from 'node:events'
import fs from 'node:fs'
import path from 'node:path'
import { getSettings } from './ipc'

// F-29：桌面壳更新检查/下载/安装（仅 Windows NSIS 安装包）。
// 安全约束：更新源必须与当前配置的服务器同源；下载后校验 sha256；安装使用 /S 静默安装。
// 不新增依赖：HTTP 用 Node 内置 fetch，哈希用 node:crypto。

export interface UpdateArtifact {
  version: string
  fileName: string
  url: string
  size: number
  sha256: string
  modifiedAt: number
}

export interface UpdateCheckResult {
  available: boolean
  currentVersion: string
  latestVersion: string
  artifact: UpdateArtifact | null
}

export interface UpdateProgress {
  downloaded: number
  total: number
  percent: number
}

export type UpdateIpcResult = { ok: true } | { ok: false; message: string }
export type UpdateCheckIpcResult = { ok: true; result: UpdateCheckResult } | { ok: false; message: string }

interface VersionManifest {
  version?: unknown
  protocolVersion?: unknown
  artifacts?: { desktop?: unknown } | null
}

const MAX_REDIRECTS = 5
const MAX_INSTALLER_BYTES = 512 * 1024 * 1024
const INSTALLER_DIR_NAME = 'erisechat-update'

let downloadedInstallerPath: string | null = null
let downloadedInstallerSha256: string | null = null

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

/** 当前配置的服务器 origin（settings.json 中的 serverUrl）。 */
function configuredServerOrigin(): URL | null {
  const raw = getSettings().serverUrl.trim()
  if (!raw) return null
  try {
    const parsed = new URL(raw)
    if (parsed.protocol !== 'http:' && parsed.protocol !== 'https:') return null
    return parsed
  } catch {
    return null
  }
}

function isSameOrigin(target: URL, origin: URL): boolean {
  return target.protocol === origin.protocol && target.hostname === origin.hostname && target.port === origin.port
}

/** 手动跟随重定向：每一次跳转都必须是当前服务器同源，避免被清单指向外部地址。 */
async function fetchSameOrigin(url: URL, origin: URL): Promise<Response> {
  let current = url
  for (let redirects = 0; redirects <= MAX_REDIRECTS; redirects += 1) {
    if (!isSameOrigin(current, origin)) throw new Error('更新地址不在当前服务器域名下')
    const response = await fetch(current, { redirect: 'manual' })
    if (response.status >= 300 && response.status < 400) {
      const location = response.headers.get('location')
      if (!location) throw new Error('更新重定向缺少目标地址')
      current = new URL(location, current)
      continue
    }
    return response
  }
  throw new Error('更新地址重定向次数过多')
}

function validateArtifact(raw: unknown, origin: URL): UpdateArtifact | null {
  if (!raw || typeof raw !== 'object') return null
  const record = raw as Record<string, unknown>
  const version = typeof record.version === 'string' ? record.version.trim() : ''
  const fileName = typeof record.fileName === 'string' ? path.basename(record.fileName.trim()) : ''
  const url = typeof record.url === 'string' ? record.url.trim() : ''
  const sha256 = typeof record.sha256 === 'string' ? record.sha256.trim().toLowerCase() : ''
  const size = typeof record.size === 'number' && Number.isFinite(record.size) ? Math.trunc(record.size) : 0
  const modifiedAt =
    typeof record.modifiedAt === 'number' && Number.isFinite(record.modifiedAt) ? Math.trunc(record.modifiedAt) : 0
  if (!version || !fileName || !url || !sha256) return null
  if (!/^EriseChat-Setup-[0-9]+\.[0-9]+\.[0-9]+\.exe$/i.test(fileName)) return null
  if (!/^[0-9a-f]{64}$/.test(sha256)) return null
  if (!url.toLowerCase().endsWith('.exe')) return null
  try {
    const resolved = new URL(url, origin)
    if (!isSameOrigin(resolved, origin)) return null
  } catch {
    return null
  }
  return { version, fileName, url, size, sha256, modifiedAt }
}

function sendProgress(sender: Electron.WebContents, downloaded: number, total: number): void {
  const percent = total > 0 ? Math.min(100, Math.round((downloaded / total) * 100)) : 0
  if (sender.isDestroyed()) return
  sender.send('update:progress', { downloaded, total, percent } satisfies UpdateProgress)
}

async function downloadInstaller(
  sender: Electron.WebContents,
  artifact: UpdateArtifact,
  origin: URL,
): Promise<UpdateIpcResult> {
  const targetUrl = new URL(artifact.url, origin)
  const response = await fetchSameOrigin(targetUrl, origin)
  if (!response.ok) return { ok: false, message: `下载失败：服务器返回 ${response.status}` }
  const totalHeader = Number(response.headers.get('content-length') ?? 0)
  const total = artifact.size > 0 ? artifact.size : Number.isFinite(totalHeader) ? totalHeader : 0
  const body = response.body
  if (!body) return { ok: false, message: '下载失败：响应内容为空' }
  const directory = path.join(app.getPath('temp'), INSTALLER_DIR_NAME)
  fs.mkdirSync(directory, { recursive: true })
  const targetPath = path.join(directory, artifact.fileName)
  const stream = fs.createWriteStream(targetPath)
  const hash = createHash('sha256')
  const reader = body.getReader()
  let downloaded = 0
  let streamError: unknown = null
  stream.on('error', (error) => {
    streamError = error
  })
  try {
    for (;;) {
      const { done, value } = await reader.read()
      if (done) break
      if (streamError) throw streamError
      if (!value) continue
      downloaded += value.byteLength
      if (downloaded > MAX_INSTALLER_BYTES) throw new Error('安装包超过大小上限')
      hash.update(Buffer.from(value))
      if (!stream.write(Buffer.from(value))) await once(stream, 'drain')
      sendProgress(sender, downloaded, total)
    }
    await new Promise<void>((resolve, reject) => {
      stream.end((error?: Error | null) => (error ? reject(error) : resolve()))
    })
  } catch (error) {
    stream.destroy()
    fs.rmSync(targetPath, { force: true })
    return { ok: false, message: `下载失败：${errorMessage(error)}` }
  }
  const sha256 = hash.digest('hex')
  if (sha256 !== artifact.sha256) {
    fs.rmSync(targetPath, { force: true })
    return { ok: false, message: '安装包校验失败（sha256 不一致），请重试' }
  }
  sendProgress(sender, downloaded, total > 0 ? total : downloaded)
  downloadedInstallerPath = targetPath
  downloadedInstallerSha256 = sha256
  return { ok: true }
}

async function checkForUpdate(): Promise<UpdateCheckIpcResult> {
  const origin = configuredServerOrigin()
  if (!origin) return { ok: false, message: '请先在设置中填写服务器地址' }
  try {
    const response = await fetchSameOrigin(new URL('/api/version', origin), origin)
    if (!response.ok) return { ok: false, message: `检查更新失败：服务器返回 ${response.status}` }
    const manifest = (await response.json()) as VersionManifest
    const artifact = validateArtifact(manifest.artifacts?.desktop, origin)
    const currentVersion = app.getVersion()
    // 只比较桌面产物版本：清单总版本会被服务端程序集或 Android 单独升版推高，用它判断会误报。
    const latestVersion = artifact?.version ?? currentVersion
    const available = Boolean(artifact) && compareVersions(latestVersion, currentVersion) > 0
    return { ok: true, result: { available, currentVersion, latestVersion, artifact } }
  } catch (error) {
    return { ok: false, message: `检查更新失败：${errorMessage(error)}` }
  }
}

async function downloadUpdate(_event: IpcMainInvokeEvent, rawArtifact: unknown): Promise<UpdateIpcResult> {
  const origin = configuredServerOrigin()
  if (!origin) return { ok: false, message: '请先在设置中填写服务器地址' }
  const artifact = validateArtifact(rawArtifact, origin)
  if (!artifact) return { ok: false, message: '更新信息无效，请重新检查更新' }
  try {
    return await downloadInstaller(_event.sender, artifact, origin)
  } catch (error) {
    return { ok: false, message: `下载失败：${errorMessage(error)}` }
  }
}

async function installUpdate(): Promise<UpdateIpcResult> {
  if (!downloadedInstallerPath || !fs.existsSync(downloadedInstallerPath)) {
    downloadedInstallerPath = null
    downloadedInstallerSha256 = null
    return { ok: false, message: '未找到已下载的安装包，请重新下载' }
  }
  try {
    const hash = createHash('sha256')
    const stream = fs.createReadStream(downloadedInstallerPath)
    for await (const chunk of stream) hash.update(chunk as Buffer)
    const actual = hash.digest('hex')
    if (!downloadedInstallerSha256 || actual !== downloadedInstallerSha256) {
      return { ok: false, message: '安装包已被修改，请重新下载' }
    }
    const installerPath = downloadedInstallerPath
    const child = spawn(installerPath, ['/S'], { detached: true, stdio: 'ignore' })
    child.unref()
    setTimeout(() => app.quit(), 1_000)
    return { ok: true }
  } catch (error) {
    return { ok: false, message: `安装失败：${errorMessage(error)}` }
  }
}

export function registerUpdateIpcHandlers(): void {
  ipcMain.handle('update:check', () => checkForUpdate())
  ipcMain.handle('update:download', (event, artifact: unknown) => downloadUpdate(event, artifact))
  ipcMain.handle('update:install', () => installUpdate())
}

/** 退出/热更新前移除 IPC 监听，避免重复注册。 */
export function disposeUpdateIpcHandlers(): void {
  ipcMain.removeHandler('update:check')
  ipcMain.removeHandler('update:download')
  ipcMain.removeHandler('update:install')
}
