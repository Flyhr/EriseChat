#!/usr/bin/env node
// 开发启动器：先编译 Electron 主进程，再用「清除 ELECTRON_RUN_AS_NODE 后」的环境启动 Electron。
//
// 为什么需要它：部分终端 / CI / AI 宿主环境会注入 ELECTRON_RUN_AS_NODE=1，
// 此时 electron.exe 会以纯 Node 模式运行，require('electron').app 为 undefined，
// 报 "Cannot read properties of undefined (reading 'requestSingleInstanceLock')"。
// 脚本内只在子进程环境里删掉该变量，不改动用户系统的环境变量。
import { spawnSync } from 'node:child_process'
import { existsSync } from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const desktopDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..')
const isWindows = process.platform === 'win32'

const env = { ...process.env }
delete env.ELECTRON_RUN_AS_NODE

function run(command, args) {
  return spawnSync(command, args, { cwd: desktopDir, env, stdio: 'inherit', shell: isWindows })
}

const build = run('npm', ['run', 'build'])
if (build.status !== 0) {
  process.exit(build.status ?? 1)
}

const electronBin = path.join(desktopDir, 'node_modules', '.bin', isWindows ? 'electron.cmd' : 'electron')
if (!existsSync(electronBin)) {
  console.error('[eisechat] 未找到 Electron 可执行文件，请先在 apps/desktop 执行 npm install')
  process.exit(1)
}

const app = run(electronBin, ['.'])
process.exit(app.status ?? 0)
