/// <reference types="vite/client" />

// EriseChat Web 环境变量类型（变量示例见 .env.example，真实 .env 不入库）。
interface ImportMetaEnv {
  readonly VITE_API_BASE?: string
  readonly VITE_WS_URL?: string
  readonly VITE_PROTOCOL_VERSION?: string
  readonly VITE_SERVER_PRESETS?: string
}

interface ImportMeta {
  readonly env: ImportMetaEnv
}
