namespace Chat.Server.Common.Options;

/// <summary>
/// 邮件发送配置（对应 appsettings*.json 的 "Email" 节）。
/// SMTP 密码等凭据只放 secrets.json（键：Email:SmtpPassword）或环境变量。
/// </summary>
public sealed class EmailOptions
{
    public const string SectionName = "Email";

    /// <summary>Mock 通道配置（仅 Development 使用：验证码输出到控制台，不真实发信）。</summary>
    public MockEmailOptions Mock { get; set; } = new();

    /// <summary>SMTP 服务器地址（为空表示未配置）。</summary>
    public string SmtpHost { get; set; } = string.Empty;

    /// <summary>SMTP 端口，默认 587（STARTTLS）。</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP 是否使用 SSL/TLS。</summary>
    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>SMTP 登录用户名。</summary>
    public string SmtpUser { get; set; } = string.Empty;

    /// <summary>SMTP 登录密码（只放 secrets.json，禁止写日志）。</summary>
    public string SmtpPassword { get; set; } = string.Empty;

    /// <summary>发件人邮箱地址（为空时使用 SmtpUser）。</summary>
    public string FromAddress { get; set; } = string.Empty;

    /// <summary>发件人显示名称。</summary>
    public string FromName { get; set; } = "EriseChat";
}

/// <summary>Mock 邮件通道配置（appsettings.Development.json 的 "Email:Mock" 节）。</summary>
public sealed class MockEmailOptions
{
    /// <summary>固定测试码（6 位数字；仅 Development 生效，留空则随机生成）。</summary>
    public string FixedCode { get; set; } = string.Empty;
}
