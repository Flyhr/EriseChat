using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// users：用户账号（表结构唯一来源：protocol/schema.sql）。
/// 时间字段均为 UTC 毫秒；<see cref="AvatarFileId"/> 为便利引用，不加外键以避免 users ↔ files 循环依赖。
/// </summary>
public sealed class User
{
    /// <summary>用户 ID（UUID 文本，小写 36 位）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>邮箱（全局唯一，注册与登录凭证）。</summary>
    public string Email { get; set; } = string.Empty;

    /// <summary>昵称。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>密码哈希（PBKDF2-SHA256；为空表示仅验证码登录，M1 不启用密码）。</summary>
    public string? PasswordHash { get; set; }

    /// <summary>邮箱验证时间，空表示未验证。</summary>
    public long? EmailVerifiedAt { get; set; }

    /// <summary>头像文件 ID（指向 files.id，无外键）。</summary>
    public string? AvatarFileId { get; set; }

    /// <summary>用户角色：user / admin。</summary>
    public UserRole Role { get; set; } = UserRole.User;

    /// <summary>用户状态：active / disabled。</summary>
    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>创建时间。</summary>
    public long CreatedAt { get; set; }
}
