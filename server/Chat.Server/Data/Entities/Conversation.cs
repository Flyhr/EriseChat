using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// conversations：会话（单聊 / 群聊）。
/// <see cref="LastMessageId"/> 为便利引用，不加外键以避免 conversations ↔ messages 循环依赖。
/// </summary>
public sealed class Conversation
{
    /// <summary>会话 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>会话类型：direct / group。</summary>
    public ConversationType Type { get; set; }

    /// <summary>群标题；单聊为空（客户端用对方昵称展示）。</summary>
    public string? Title { get; set; }

    /// <summary>群公告（≤500 字符，可换行）；NULL = 无公告；单聊恒为 NULL（F-14）。</summary>
    public string? Announcement { get; set; }

    /// <summary>群头像 files.id（便利引用，无外键）；NULL = 未设置；单聊恒为 NULL（F-23）。</summary>
    public string? AvatarFileId { get; set; }

    /// <summary>单聊唯一键（两个用户 ID 排序后拼接，如 "u1:u2"）；群聊为空。</summary>
    public string? DirectKey { get; set; }

    /// <summary>创建者用户 ID。</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>会话最新消息序号，只增不减（未读与补拉基数）。</summary>
    public long LastSeq { get; set; }

    /// <summary>最新消息 ID（指向 messages.id，无外键）。</summary>
    public string? LastMessageId { get; set; }
}
