namespace Chat.Protocol.Frames;

/// <summary>
/// 拉黑条目（C1b，protocol/protocol.md §7 第 15 条）：
/// GET /api/blocks 的数组元素与 PUT /api/blocks/{userId} 的返回值。
/// 只描述「当前账号拉黑了谁」，不包含对方视角，也不下发邮箱。
/// </summary>
/// <param name="UserId">被拉黑的用户 ID。</param>
/// <param name="DisplayName">被拉黑用户昵称。</param>
/// <param name="AvatarFileId">被拉黑用户头像文件 ID（可空）。</param>
/// <param name="CreatedAt">拉黑时间（UTC 毫秒）。</param>
public sealed record BlockedUser(
    string UserId,
    string DisplayName,
    string? AvatarFileId,
    long CreatedAt);
