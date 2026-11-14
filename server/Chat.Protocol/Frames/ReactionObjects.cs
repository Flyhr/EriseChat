namespace Chat.Protocol.Frames;

/// <summary>
/// 消息回应摘要（protocol/protocol.md §4.5，C3）：按表情聚合的人数与当前账号的选择。
/// 只下发摘要，不返回回应者名单；count 为 0 的表情不出现。
/// </summary>
/// <param name="Emoji">稳定表情标识（见 <see cref="ReactionEmoji"/>）。</param>
/// <param name="Count">该表情的回应人数。</param>
/// <param name="Mine">当前请求账号是否选择了该表情（按接收账号计算）。</param>
public sealed record ReactionSummary(string Emoji, int Count, bool Mine);
