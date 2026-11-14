namespace Chat.Server.Data.Entities;

/// <summary>
/// group_invite_links：群邀请链接（C1c）。token 为不含邮箱的随机串（主键），可撤销、可过期。
/// 同一链接可被多人使用，成员占位与上限校验在兑换事务内完成。
/// </summary>
public sealed class GroupInviteLink
{
    /// <summary>随机令牌（主键，base64url，不含邮箱等个人信息）。</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>目标群会话 ID。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>创建者用户 ID；兑换时校验其当前是否仍有邀请资格。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>过期时间（UTC 毫秒）。</summary>
    public long ExpiresAt { get; set; }

    /// <summary>撤销时间（UTC 毫秒）；NULL 表示未撤销。</summary>
    public long? RevokedAt { get; set; }

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
