import type { CapacitorConfig } from '@capacitor/cli'

// EiseChat Android 壳（M0 骨架）：只做容器与打包，业务全部在 apps/web（project-overview §7.3、§10）。
// Web 资源随 APK 打包，不依赖在线加载（AGENTS.md §3）；服务器地址在登录页/设置页填写。
// 注意：appId 使用 com.eisechat.mobile，与 Windows 壳的 com.eisechat.desktop 区分（避免包名混淆）。
const config: CapacitorConfig = {
  appId: 'com.eisechat.mobile',
  appName: 'EiseChat',
  webDir: '../web/dist',
}

export default config
