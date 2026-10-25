using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>conversation_members：会话成员（主键 conversation_id + user_id）。</summary>
public sealed class ConversationMember
{
    /// <summary>会话 ID。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>用户 ID。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>会话内角色：owner / member。</summary>
    public MemberRole Role { get; set; } = MemberRole.Member;

    /// <summary>加入时间（UTC 毫秒）。</summary>
    public long JoinedAt { get; set; }

    /// <summary>已读游标，只增不减（未读 = 会话 last_seq − last_read_seq）。</summary>
    public long LastReadSeq { get; set; }
}
