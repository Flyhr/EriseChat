namespace Chat.Protocol;

/// <summary>消息类型（JSON 中为小写字符串，映射在 DTO/序列化层完成）。</summary>
public enum MessageType
{
    /// <summary>纯文本。</summary>
    Text,

    /// <summary>图片（content 为 FileContent，二进制走 REST，不走 WS）。</summary>
    Image,

    /// <summary>文件（content 为 FileContent，二进制走 REST，不走 WS）。</summary>
    File,

    /// <summary>语音（content 为 VoiceContent，音频文件走 REST；F-28）。</summary>
    Voice,

    /// <summary>系统消息（仅服务端生成）。</summary>
    System,
}

/// <summary>会话类型。</summary>
public enum ConversationType
{
    /// <summary>单聊。</summary>
    Direct,

    /// <summary>群聊。</summary>
    Group,
}

/// <summary>会话成员角色。</summary>
public enum MemberRole
{
    /// <summary>群主。</summary>
    Owner,

    /// <summary>普通成员。</summary>
    Member,
}

/// <summary>用户角色。</summary>
public enum UserRole
{
    /// <summary>普通用户。</summary>
    User,

    /// <summary>管理员。</summary>
    Admin,
}

/// <summary>用户状态。</summary>
public enum UserStatus
{
    /// <summary>正常。</summary>
    Active,

    /// <summary>已禁用（不可登录、不可收发消息）。</summary>
    Disabled,
}

/// <summary>好友申请状态（storage: friend_requests.status，JSON 中为小写字符串）。</summary>
public enum FriendRequestStatus
{
    /// <summary>待处理。</summary>
    Pending,

    /// <summary>已接受。</summary>
    Accepted,

    /// <summary>已拒绝。</summary>
    Rejected,

    /// <summary>申请人已撤销。</summary>
    Cancelled,
}

/// <summary>协议版本常量（唯一来源：protocol/protocol.md §8）。</summary>
public static class ProtocolVersion
{
    /// <summary>当前协议版本（v1）。</summary>
    public const int Current = 1;
}

/// <summary>
/// 错误码常量（唯一来源：protocol/protocol.md §5）。
/// WS 的 error 帧与 REST 错误响应共用同一枚举；前端 apps/web/src/types/frames.d.ts 保持一致。
/// </summary>
public static class ErrorCodes
{
    // ---------- §5.1 WebSocket 错误码 ----------

    /// <summary>帧不是合法 JSON、缺 type 或字段类型错误（致命）。</summary>
    public const string BadFrame = "BAD_FRAME";

    /// <summary>认证前发送了非 auth 帧，或 10 秒未认证（致命）。</summary>
    public const string AuthRequired = "AUTH_REQUIRED";

    /// <summary>token 无效、deviceId 缺失或格式非法（致命）。</summary>
    public const string AuthFailed = "AUTH_FAILED";

    /// <summary>JWT 过期或会话已被撤销（致命）。</summary>
    public const string AuthExpired = "AUTH_EXPIRED";

    /// <summary>protocolVersion 不兼容（致命）。</summary>
    public const string ProtocolVersionMismatch = "PROTOCOL_VERSION_MISMATCH";

    /// <summary>无权限执行该操作。</summary>
    public const string Forbidden = "FORBIDDEN";

    /// <summary>双方存在拉黑关系，禁止私聊、好友申请或群邀请（C1b，REST 与 WS 共用）。</summary>
    public const string Blocked = "BLOCKED";

    /// <summary>不是目标会话成员。</summary>
    public const string NotMember = "NOT_MEMBER";

    /// <summary>资源不存在。</summary>
    public const string NotFound = "NOT_FOUND";

    /// <summary>字段缺失、类型错误、数值越界等。</summary>
    public const string ValidationFailed = "VALIDATION_FAILED";

    /// <summary>消息内容超过服务端配置上限。</summary>
    public const string MessageTooLarge = "MESSAGE_TOO_LARGE";

    /// <summary>操作过于频繁。</summary>
    public const string RateLimited = "RATE_LIMITED";

    /// <summary>服务端内部错误（日志带 traceId）。</summary>
    public const string InternalError = "INTERNAL_ERROR";

    // ---------- §5.2 账号与 REST 错误码 ----------

    /// <summary>未登录或 REST 请求缺少有效 token。</summary>
    public const string Unauthorized = "UNAUTHORIZED";

    /// <summary>邮箱验证码错误。</summary>
    public const string CodeInvalid = "CODE_INVALID";

    /// <summary>邮箱验证码已过期。</summary>
    public const string CodeExpired = "CODE_EXPIRED";

    /// <summary>验证码错误次数超限，已作废。</summary>
    public const string CodeAttemptsExceeded = "CODE_ATTEMPTS_EXCEEDED";

    /// <summary>验证码发送限流。</summary>
    public const string EmailRateLimited = "EMAIL_RATE_LIMITED";

    /// <summary>邮箱域名不在白名单内。</summary>
    public const string EmailDomainNotAllowed = "EMAIL_DOMAIN_NOT_ALLOWED";

    /// <summary>注册功能已关闭。</summary>
    public const string RegistrationDisabled = "REGISTRATION_DISABLED";

    /// <summary>邮箱已注册。</summary>
    public const string EmailAlreadyRegistered = "EMAIL_ALREADY_REGISTERED";

    /// <summary>当前注册需要邀请码。</summary>
    public const string InviteRequired = "INVITE_REQUIRED";

    /// <summary>邀请码无效或已被使用。</summary>
    public const string InviteInvalid = "INVITE_INVALID";

    /// <summary>邀请码已过期。</summary>
    public const string InviteExpired = "INVITE_EXPIRED";

    /// <summary>密码错误。</summary>
    public const string PasswordInvalid = "PASSWORD_INVALID";

    /// <summary>登录失败次数过多，临时锁定。</summary>
    public const string AccountLocked = "ACCOUNT_LOCKED";

    /// <summary>账号已被禁用。</summary>
    public const string AccountDisabled = "ACCOUNT_DISABLED";

    /// <summary>上传文件超过大小上限。</summary>
    public const string FileTooLarge = "FILE_TOO_LARGE";

    /// <summary>文件 MIME 或扩展名不在允许范围。</summary>
    public const string FileTypeNotAllowed = "FILE_TYPE_NOT_ALLOWED";
}
