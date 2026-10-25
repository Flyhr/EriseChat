namespace Chat.Server.Data.Entities;

/// <summary>验证码用途（对应 email_codes.purpose 的 register / login）。</summary>
public enum EmailCodePurpose
{
    /// <summary>注册。</summary>
    Register,

    /// <summary>登录。</summary>
    Login,
}

/// <summary>email_codes：邮箱验证码（只存哈希，不存明文）。</summary>
public sealed class EmailCode
{
    /// <summary>验证码记录 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>接收邮箱。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>用途：register / login。</summary>
    public EmailCodePurpose Purpose { get; set; }

    /// <summary>验证码哈希（不存明文）。</summary>
    public string CodeHash { get; set; } = string.Empty;

    /// <summary>过期时间（UTC 毫秒）。</summary>
    public long ExpiresAt { get; set; }

    /// <summary>已尝试校验次数（错误 5 次作废，见 AGENTS §6）。</summary>
    public int Attempts { get; set; }

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>请求来源 IP（可为空，便于限流排查）。</summary>
    public string? Ip { get; set; }
}
