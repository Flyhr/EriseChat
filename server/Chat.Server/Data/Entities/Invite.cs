namespace Chat.Server.Data.Entities;

/// <summary>invites：注册邀请码（可配置是否需要邀请；code 为明文短码，主键）。</summary>
public sealed class Invite
{
    /// <summary>邀请码（主键）。</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>创建者用户 ID。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>使用者用户 ID（已用邀请码只能使用一次）。</summary>
    public string? UsedBy { get; set; }

    /// <summary>过期时间（UTC 毫秒）；NULL 表示不限期。</summary>
    public long? ExpiresAt { get; set; }

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
