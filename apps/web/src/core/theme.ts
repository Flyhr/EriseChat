import type { GlobalThemeOverrides } from 'naive-ui'

/** 当前主题标识，与 index.html 的 data-ec-theme 一致；本轮不提供切换入口。 */
export const EC_THEME_ID = 'azure'

/**
 * Naive UI 主题覆盖：把 --ec-* 设计令牌映射为组件库主题，保证新增组件与既有蓝白界面同色。
 * 使用具体色值而非 var()，因为组件库会对 primaryColor 做 alpha 混合生成派生色，
 * 传入 var() 会得到无效颜色。新增主题时在此处按主题返回对应 overrides，业务组件不感知。
 * 色值与 styles/themes/azure.css 保持一致。
 */
export const ecThemeOverrides: GlobalThemeOverrides = {
  common: {
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
    borderRadius: '12px',
    borderRadiusSmall: '8px',
    fontFamily:
      "'Inter', 'Segoe UI', 'Microsoft YaHei', 'PingFang SC', system-ui, -apple-system, sans-serif",
    fontSize: '14px',
    fontSizeSmall: '12px',
    fontSizeMedium: '14px',
  },
}
