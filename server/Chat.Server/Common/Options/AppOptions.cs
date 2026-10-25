namespace Chat.Server.Common.Options;

/// <summary>
/// 应用级配置（对应 appsettings*.json 的 "App" 节）。
/// 目录类配置允许相对路径：启动时相对内容根目录解析为绝对路径（默认仓库根 data/）。
/// </summary>
public sealed class AppOptions
{
    public const string SectionName = "App";

    /// <summary>运行时数据目录（SQLite 数据库所在目录），默认仓库根 data/。</summary>
    public string DataDirectory { get; set; } = "../../data";

    /// <summary>上传文件存储目录（/data/files/yyyyMM/{fileId}{ext}），默认仓库根 data/files。</summary>
    public string FileDirectory { get; set; } = "../../data/files";

    /// <summary>单文件上传上限（字节），默认 50MB（AGENTS §6）。</summary>
    public long UploadMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>是否开放注册（管理员可关闭，关闭后注册接口返回 REGISTRATION_DISABLED）。</summary>
    public bool RegistrationEnabled { get; set; } = true;

    /// <summary>允许注册/登录的邮箱域名白名单；空数组表示不限制域名。</summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>协议版本，握手与 /api/health 使用（protocol/protocol.md §8）。</summary>
    public int ProtocolVersion { get; set; } = 1;
}
