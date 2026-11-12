import type { GlobalThemeOverrides } from 'naive-ui'
import type { EcTheme } from '../stores/settings'

/**
 * Naive UI 主题覆盖：把 --ec-* 设计令牌映射为组件库主题，保证组件库控件与界面同色。
 * 使用具体色值而非 var()，因为组件库会对 primaryColor 做 alpha 混合生成派生色，
 * 传入 var() 会得到无效颜色。色值分别与 styles/themes/azure.css、dark.css 保持一致。
 */
const COMMON = {
  borderRadius: '12px',
  borderRadiusSmall: '8px',
  fontFamily:
    "'Inter', 'Segoe UI', 'Microsoft YaHei', 'PingFang SC', system-ui, -apple-system, sans-serif",
  fontSize: '14px',
  fontSizeSmall: '12px',
  fontSizeMedium: '14px',
}

const AZURE: GlobalThemeOverrides = {
  common: {
    ...COMMON,
    primaryColor: '#2563eb',
    primaryColorHover: '#3b82f6',
    primaryColorPressed: '#1d4ed8',
    primaryColorSuppl: '#2563eb',
    successColor: '#059669',
    warningColor: '#b45309',
    errorColor: '#dc2626',
    textColorBase: '#0f172a',
    textColor1: '#0f172a',
    textColor2: '#5f6d7e',
    textColor3: '#94a3b8',
    borderColor: '#e2e8f0',
    dividerColor: '#eef3fb',
    bodyColor: '#ffffff',
    inputColor: '#ffffff',
    popoverColor: '#ffffff',
  },
}

const DARK: GlobalThemeOverrides = {
  common: {
    ...COMMON,
    primaryColor: '#2563eb',
    primaryColorHover: '#3b82f6',
    primaryColorPressed: '#1d4ed8',
    primaryColorSuppl: '#2563eb',
    successColor: '#34d399',
    warningColor: '#fbbf24',
    errorColor: '#f87171',
    textColorBase: '#e8eef9',
    textColor1: '#e8eef9',
    textColor2: '#a8b8cf',
    textColor3: '#7c8da8',
    borderColor: '#2a3a55',
    dividerColor: '#1e2c44',
    bodyColor: '#0b1220',
    inputColor: '#16223a',
    popoverColor: '#16223a',
  },
}

/** 按当前主题返回组件库 overrides；App.vue 用 computed 传入 NConfigProvider。 */
export function ecThemeOverridesFor(theme: EcTheme): GlobalThemeOverrides {
  return theme === 'dark' ? DARK : AZURE
}
