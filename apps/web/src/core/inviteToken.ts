// 群邀请链接令牌的格式校验（C1c）：32 字节随机数的 base64url 编码（无填充），固定 43 个字符。
// 前端只做格式预判，是否有效、是否过期/撤销仍由服务端校验；路由与登录回跳只传递通过该校验的值。

const INVITE_TOKEN_PATTERN = /^[A-Za-z0-9_-]{43}$/

/** 是否为形如群邀请链接令牌的字符串。 */
export function isInviteTokenLike(value: unknown): value is string {
  return typeof value === 'string' && INVITE_TOKEN_PATTERN.test(value)
}
