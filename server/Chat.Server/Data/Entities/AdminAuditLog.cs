namespace Chat.Server.Data.Entities;

/// <summary>admin_audit_logs：管理员操作审计（detail_json 不得包含 token/密码/验证码等敏感值）。</summary>
public sealed class AdminAuditLog
{
    /// <summary>日志 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>操作管理员用户 ID。</summary>
    public string AdminId { get; set; } = string.Empty;

    /// <summary>操作类型（英文标识，如 user.disable）。</summary>
    public string Action { get; set; } = string.Empty;

    /// <summary>操作对象标识（可为空）。</summary>
    public string? Target { get; set; }

    /// <summary>操作详情 JSON（可为空）。</summary>
    public string? DetailJson { get; set; }

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
