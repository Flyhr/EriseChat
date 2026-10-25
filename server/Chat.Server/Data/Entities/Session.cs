namespace Chat.Server.Data.Entities;

/// <summary>sessions：登录会话（JWT 可撤销；jwt_id 对应 JWT 的 jti）。</summary>
public sealed class Session
{
    /// <summary>会话 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属用户 ID。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>客户端设备标识（同一设备重连复用）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>JWT 的 jti（唯一）。</summary>
    public string JwtId { get; set; } = string.Empty;

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>最近活跃时间（UTC 毫秒）。</summary>
    public long LastSeenAt { get; set; }

    /// <summary>撤销时间（UTC 毫秒），非空表示已撤销/注销。</summary>
    public long? RevokedAt { get; set; }
}
