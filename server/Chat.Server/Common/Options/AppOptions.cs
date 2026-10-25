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

    /// <summary>安装包分发目录（Windows exe / Android apk，供 /download 页使用），默认仓库根 data/downloads（M4-04）。</summary>
    public string DownloadDirectory { get; set; } = "../../data/downloads";

    /// <summary>单文件上传上限（字节），默认 50MB（AGENTS §6）。</summary>
    public long UploadMaxBytes { get; set; } = 50L * 1024 * 1024;

    /// <summary>孤儿文件清理轮询间隔（小时），默认 24；代码内限制 1 小时~7 天（M3-02）。</summary>
    public int OrphanFileCleanupIntervalHours { get; set; } = 24;

    /// <summary>是否开放注册（管理员可关闭，关闭后注册接口返回 REGISTRATION_DISABLED）。</summary>
    public bool RegistrationEnabled { get; set; } = true;

    /// <summary>允许注册/登录的邮箱域名白名单；空数组表示不限制域名。</summary>
    public string[] AllowedEmailDomains { get; set; } = [];

    /// <summary>协议版本，握手与 /api/health 使用（protocol/protocol.md §8）。</summary>
    public int ProtocolVersion { get; set; } = 1;

    /// <summary>群成员上限（含群主），默认 100；使用时限制在 2~500（M3-04）。</summary>
    public int GroupMaxMembers { get; set; } = 100;

    /// <summary>是否仅群主可邀请成员，默认 false = 所有成员可邀请（M3-04）。</summary>
    public bool GroupOnlyOwnerCanInvite { get; set; }
}
