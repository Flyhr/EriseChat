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
/// 会话对象（protocol/protocol.md §4.5）。未读数由服务端按收件人实时计算并下发：
/// 只统计他人发送的、seq 大于我的 lastReadSeq 的消息，自己发送的消息不计入。
/// </summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="Type">direct（单聊）/ group（群聊）。</param>
/// <param name="Title">群标题；单聊为空字符串（客户端用对方昵称展示）。</param>
/// <param name="CreatedAt">创建时间（UTC 毫秒）。</param>
/// <param name="LastSeq">会话最新序号。</param>
/// <param name="LastReadSeq">当前用户在该会话的已读游标。</param>
/// <param name="Members">成员数组。</param>
/// <param name="LastMessage">最新消息快照，可为 null。</param>
/// <param name="Pinned">当前用户是否置顶该会话（F-10，成员级设置，默认 false；按收件人填充，不广播）。</param>
/// <param name="Muted">当前用户是否对该会话免打扰（F-10，成员级设置，默认 false；按收件人填充，不广播）。</param>
/// <param name="Announcement">群公告（F-14，≤500 字符，支持换行）；null 表示无公告；单聊恒为 null。</param>
/// <param name="AvatarFileId">群头像 files.id（F-23）；null 表示未设置；单聊恒为 null。</param>
/// <param name="Unread">当前用户的未读数（F-26）：只统计他人发送且 seq 大于 lastReadSeq 的消息，自己发送的消息不计入；按收件人计算。</param>
/// <param name="MarkedUnread">当前用户是否手动标为未读：只影响未读筛选与提示，不改 lastReadSeq、不增加 unread。</param>
public sealed record Conversation(
    string ConversationId,
    ConversationType Type,
    string Title,
    long CreatedAt,
    long LastSeq,
    long LastReadSeq,
    IReadOnlyList<ConversationMember> Members,
    Message? LastMessage = null,
    bool Pinned = false,
    bool Muted = false,
    string? Announcement = null,
    string? AvatarFileId = null,
    long Unread = 0,
    bool MarkedUnread = false);
