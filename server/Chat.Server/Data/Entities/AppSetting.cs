namespace Chat.Server.Data.Entities;

/// <summary>app_settings：全局配置（key/value，value 为 JSON 文本；敏感值仍放 secrets.json）。</summary>
public sealed class AppSetting
{
    /// <summary>配置键（主键）。</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>配置值（JSON 文本）。</summary>
    public string Value { get; set; } = string.Empty;
}
