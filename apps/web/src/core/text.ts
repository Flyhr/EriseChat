// 文本展示工具：按 Unicode 码点安全截断（不拆代理对），超长追加省略号。
// 会话列表摘要（80）、收藏摘要（90）、引用条（60）共用，长度由调用方传入。

/** 按码点截断到 max 个字符；超出追加「…」。max 非法时按 1 处理（至少保留一个字符）。 */
export function truncateText(text: string, max: number): string {
  const limit = Number.isFinite(max) && max > 0 ? Math.floor(max) : 1
  const chars = Array.from(text)
  if (chars.length <= limit) return text
  return chars.slice(0, limit).join('') + '…'
}
