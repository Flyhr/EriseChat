namespace Chat.Protocol.Frames;

/// <summary>会话成员对象（protocol/protocol.md §4.5）。</summary>
/// <param name="UserId">用户 ID。</param>
/// <param name="DisplayName">昵称。</param>
/// <param name="Email">邮箱（内部版可直接下发）。</param>
/// <param name="Role">会话内角色。</param>
/// <param name="JoinedAt">加入时间（UTC 毫秒）。</param>
/// <param name="AvatarFileId">头像文件 ID（可选）。</param>
public sealed record ConversationMember(
    string UserId,
    string DisplayName,
    string Email,
    MemberRole Role,
    long JoinedAt,
    string? AvatarFileId = null);

/// <summary>
/// 会话对象（protocol/protocol.md §4.5）。未读数由客户端按
/// <c>未读 = lastSeq − lastReadSeq</c> 计算，协议不单独下发 unread。
/// </summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="Type">direct（单聊）/ group（群聊）。</param>
/// <param name="Title">群标题；单聊为空字符串（客户端用对方昵称展示）。</param>
/// <param name="CreatedAt">创建时间（UTC 毫秒）。</param>
/// <param name="LastSeq">会话最新序号。</param>
/// <param name="LastReadSeq">当前用户在该会话的已读游标。</param>
/// <param name="Members">成员数组。</param>
/// <param name="LastMessage">最新消息快照，可为 null。</param>
public sealed record Conversation(
    string ConversationId,
    ConversationType Type,
    string Title,
    long CreatedAt,
    long LastSeq,
    long LastReadSeq,
    IReadOnlyList<ConversationMember> Members,
    Message? LastMessage = null);
