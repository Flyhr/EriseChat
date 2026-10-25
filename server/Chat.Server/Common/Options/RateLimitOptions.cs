namespace Chat.Server.Common.Options;

/// <summary>
/// 限流阈值配置（对应 appsettings*.json 的 "RateLimit" 节，默认值按 AGENTS §6）。
/// </summary>
public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimit";

    /// <summary>同一邮箱发送验证码最小间隔（秒），默认 60。</summary>
    public int EmailCodePerEmailIntervalSeconds { get; set; } = 60;

    /// <summary>同一邮箱每天验证码发送上限，默认 10。</summary>
    public int EmailCodePerEmailDaily { get; set; } = 10;

    /// <summary>同一 IP 每小时验证码发送上限，默认 20。</summary>
    public int EmailCodePerIpHourly { get; set; } = 20;

    /// <summary>单个验证码允许的最大错误次数，超过后验证码作废，默认 5。</summary>
    public int EmailCodeMaxAttempts { get; set; } = 5;

    /// <summary>登录失败多少次后临时锁定，默认 5。</summary>
    public int LoginFailedMaxAttempts { get; set; } = 5;

    /// <summary>登录失败锁定分钟数，默认 5。</summary>
    public int LoginLockMinutes { get; set; } = 5;
}
