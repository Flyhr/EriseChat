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

    /// <summary>是否置顶该会话（F-10：当前用户自己的成员级设置，默认 false）。</summary>
    public bool Pinned { get; set; }

    /// <summary>是否消息免打扰（F-10：当前用户自己的成员级设置，默认 false）。</summary>
    public bool Muted { get; set; }

    /// <summary>是否手动标为未读（成员级设置，默认 false）：只影响未读筛选与提示，
    /// 不改 last_read_seq、不撤销对端已读回执、不增加真实未读数。</summary>
    public bool MarkedUnread { get; set; }
}
