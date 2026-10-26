// 图标定义（UI-01，计划 §3.2 / §16.4）：统一内联 SVG 图形数据，供 AppIcon.vue 渲染。
// 只描述图形，不含样式与业务；18px 基准、1.8 描边、currentColor。
// 单独放 core/ 是为了类型可被其它组件/页面 import（SFC setup 块不能直接 export 类型）。

export type AppIconName =
  | 'chat'
  | 'contacts'
  | 'groups'
  | 'settings'
  | 'search'
  | 'plus'
  | 'image'
  | 'file'
  | 'paperclip'
  | 'emoji'
  | 'close'
  | 'more'
  | 'send'
  | 'download'
  | 'user'
  | 'logout'
  | 'check'

export interface AppIconShape {
  paths?: string[]
  circles?: { cx: number; cy: number; r: number }[]
}

export const APP_ICONS: Record<AppIconName, AppIconShape> = {
  chat: { paths: ['M4.5 5.5h15v10h-8.2l-3.6 3.2v-3.2H4.5v-10Z'] },
  contacts: {
    paths: ['M12 11.5a3.5 3.5 0 1 0 0-7 3.5 3.5 0 0 0 0 7Z', 'M5 20c0-3 3.1-5.5 7-5.5s7 2.5 7 5.5'],
  },
  groups: {
    paths: [
      'M9.5 11a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z',
      'M3 19.5c0-2.8 2.9-5 6.5-5s6.5 2.2 6.5 5',
      'M15.5 5.4a2.8 2.8 0 0 1 0 5.2',
      'M16.8 14.9c2.2.6 3.7 2.6 3.7 5.1',
    ],
  },
  settings: {
    paths: [
      'M19.4 15a1.7 1.7 0 0 0 .3 1.9l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.9-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1-1.5 1.7 1.7 0 0 0-1.9.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.9 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1 1.7 1.7 0 0 0-.3-1.9l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.9.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.9-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.9V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z',
    ],
    circles: [{ cx: 12, cy: 12, r: 3 }],
  },
  search: { paths: ['M20 20l-3.8-3.8'], circles: [{ cx: 11, cy: 11, r: 6.5 }] },
  plus: { paths: ['M12 5v14', 'M5 12h14'] },
  image: {
    paths: ['M4.5 5.5h15v13h-15v-13Z', 'M4.5 15l4.3-4.3 3.2 3.2 2.9-2.9 5 5.1'],
    circles: [{ cx: 9, cy: 9, r: 1.2 }],
  },
  file: { paths: ['M7 3.5h6.5L18 8v12.5H7V3.5Z', 'M13.5 3.5V8H18'] },
  paperclip: { paths: ['M9 12.5l5.3-5.3a3 3 0 1 1 4.2 4.2l-6.6 6.6a4.5 4.5 0 1 1-6.4-6.4l6.2-6.2'] },
  emoji: {
    paths: ['M8.8 14.2a4.2 4.2 0 0 0 6.4 0', 'M9.2 9.6h.01', 'M14.8 9.6h.01'],
    circles: [{ cx: 12, cy: 12, r: 8 }],
  },
  close: { paths: ['M6.5 6.5l11 11', 'M17.5 6.5l-11 11'] },
  more: {
    circles: [
      { cx: 5.5, cy: 12, r: 1.1 },
      { cx: 12, cy: 12, r: 1.1 },
      { cx: 18.5, cy: 12, r: 1.1 },
    ],
  },
  send: { paths: ['M4 11.7l15.5-7-5.2 15.3-3.1-6-7.2-2.3Z'] },
  download: { paths: ['M12 4v11', 'M7.5 11.5L12 16l4.5-4.5', 'M5 19.5h14'] },
  user: {
    paths: ['M12 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z', 'M5 20c0-3.3 3.1-6 7-6s7 2.7 7 6'],
  },
  logout: { paths: ['M15 4h4v16h-4', 'M4 12h11', 'M11.5 8.5L15 12l-3.5 3.5'] },
  check: { paths: ['M5 12.5l4.5 4.5L19 6.5'] },
}
