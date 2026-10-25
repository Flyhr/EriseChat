namespace Chat.Server.Common.Options;

/// <summary>
/// JWT 配置（对应 appsettings*.json 的 "Jwt" 节；密钥 Key 只放在 secrets.json，不在 appsettings 中）。
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    /// <summary>签发者（iss）。</summary>
    public string Issuer { get; set; } = "eisechat";

    /// <summary>受众（aud）。</summary>
    public string Audience { get; set; } = "eisechat";

    /// <summary>有效期（天），默认 7 天。</summary>
    public int ExpireDays { get; set; } = 7;

    /// <summary>
    /// HS256 签名密钥（Base64 32 字节）。首次启动由 SecretsBootstrapper 生成写入 secrets.json；
    /// 本属性禁止写入日志或任何响应。
    /// </summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>允许的时钟偏移（秒），用于校验 exp/nbf。</summary>
    public int ClockSkewSeconds { get; set; } = 30;
}
