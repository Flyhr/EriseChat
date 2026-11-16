#!/usr/bin/env node
/**
 * Android 开发期热更新（docs/mobile.md §5.9）。
 *
 * 把 debug 变体的 WebView 指向本机 Vite dev server（http://localhost:5173），安装 EriseChat Dev
 * 并建立 adb 端口隧道。之后改 apps/web 保存即生效，不用重新打包；正式包不受影响，仍由
 * infra/build-apk.sh 把 Web 资源打进 APK。
 *
 * 实现要点：
 * - 只在 src/debug/assets 下生成覆盖用的 capacitor.config.json（Gradle 资源合并时覆盖 main），
 *   不修改 main 的配置，也不依赖 cap run 的 live-reload（它在 Windows 上要 PATH 里有 sh）。
 * - SDK 路径从 android/local.properties 读取，不需要另设 ANDROID_HOME。
 */
import { spawnSync } from 'node:child_process';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const DEV_URL = 'http://localhost:5173';
const APP_ID = 'com.erisechat.mobile';
const DEV_APP_ID = `${APP_ID}.dev`; // app/build.gradle 的 debug applicationIdSuffix
const MAIN_ACTIVITY = `${APP_ID}.MainActivity`;

const mobileDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const androidDir = path.join(mobileDir, 'android');
const mainConfigPath = path.join(androidDir, 'app/src/main/assets/capacitor.config.json');
const debugConfigPath = path.join(androidDir, 'app/src/debug/assets/capacitor.config.json');

function abort(message) {
  console.error(`\n[dev:android] ${message}\n`);
  process.exit(1);
}

function run(command, args, options = {}) {
  const result = spawnSync(command, args, { stdio: 'inherit', ...options });
  if (result.error) abort(`无法执行 ${command}：${result.error.message}`);
  if (result.status !== 0) abort(`${command} ${args.join(' ')} 退出码 ${result.status}`);
}

/** 从 local.properties 的 sdk.dir 读 SDK 路径（Java properties 需反转义），退回 ANDROID_HOME。 */
function findAndroidSdk() {
  const localProperties = path.join(androidDir, 'local.properties');
  if (existsSync(localProperties)) {
    const line = readFileSync(localProperties, 'utf8')
      .split(/\r?\n/)
      .find((item) => item.trim().startsWith('sdk.dir='));
    if (line) {
      const sdkDir = line
        .slice(line.indexOf('=') + 1)
        .trim()
        .replace(/\\\\/g, '\\')
        .replace(/\\:/g, ':');
      if (existsSync(sdkDir)) return sdkDir;
    }
  }
  for (const key of ['ANDROID_HOME', 'ANDROID_SDK_ROOT']) {
    if (process.env[key] && existsSync(process.env[key])) return process.env[key];
  }
  abort('找不到 Android SDK：设置 apps/mobile/android/local.properties 的 sdk.dir，或设置 ANDROID_HOME');
}

/** 生成 debug 变体覆盖用的配置：在 main 的配置上只加 server.url。 */
function writeDebugConfig() {
  if (!existsSync(mainConfigPath)) {
    abort('缺少 app/src/main/assets/capacitor.config.json，先在 apps/mobile 运行 npx cap sync android');
  }
  const config = JSON.parse(readFileSync(mainConfigPath, 'utf8'));
  config.server = { ...config.server, url: DEV_URL };
  mkdirSync(path.dirname(debugConfigPath), { recursive: true });
  writeFileSync(debugConfigPath, `${JSON.stringify(config, null, 2)}\n`);
}

async function assertDevServer() {
  try {
    // 探 127.0.0.1：Vite 只绑 IPv4 回环，Node 解析 localhost 可能先走 ::1。
    const response = await fetch('http://127.0.0.1:5173', { signal: AbortSignal.timeout(2000) });
    if (response.ok) return;
  } catch {
    // 落到下面的提示
  }
  abort(`本机 Vite 未就绪（${DEV_URL}），先另开终端运行 apps/web 的 npm run dev`);
}

async function main() {
  if (!process.env.JAVA_HOME && spawnSync('java', ['-version'], { stdio: 'ignore' }).status !== 0) {
    abort('未找到 JDK：先设置 JAVA_HOME（本机为 D:\\softWare\\jdk21）再重试');
  }

  await assertDevServer();

  const adb = path.join(findAndroidSdk(), 'platform-tools', process.platform === 'win32' ? 'adb.exe' : 'adb');
  const devices = spawnSync(adb, ['devices'], { encoding: 'utf8' });
  if (devices.error) abort(`无法执行 ${adb}：${devices.error.message}`);
  const connected = devices.stdout.split(/\r?\n/).filter((line) => /\tdevice$/.test(line));
  if (!connected.length) abort('没有已授权的设备：连上手机并在弹窗点「允许 USB 调试」（adb devices）');

  // 先建隧道：dev 包启动时 Vite 必须可达，否则白屏。
  run(adb, ['reverse', 'tcp:5173', 'tcp:5173']);
  writeDebugConfig();

  // Windows 下 .bat 不能直接 spawn（Node 18+ 会报 EINVAL），交给 cmd.exe 执行。
  if (process.platform === 'win32') {
    run('cmd.exe', ['/c', 'gradlew.bat installDebug'], { cwd: androidDir });
  } else {
    run('./gradlew', ['installDebug'], { cwd: androidDir });
  }
  run(adb, ['shell', 'am', 'start', '-n', `${DEV_APP_ID}/${MAIN_ACTIVITY}`]);

  console.log(`\n[dev:android] 已启动 EriseChat Dev，加载 ${DEV_URL}。`);
  console.log('[dev:android] 改 apps/web 保存即生效；拔插 USB 后重跑本命令以恢复隧道。\n');
}

await main();
