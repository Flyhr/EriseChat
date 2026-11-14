namespace Chat.Protocol.Frames;

/// <summary>群邀请链接对象（C1c，protocol/protocol.md §7 第 16 条）：不含邮箱等个人信息。</summary>
/// <param name="Token">随机令牌，用于拼链接。</param>
/// <param name="ConversationId">目标群会话 ID。</param>
/// <param name="ExpiresAt">过期时间（UTC 毫秒）。</param>
/// <param name="RevokedAt">撤销时间（UTC 毫秒）；null 表示未撤销。</param>
/// <param name="CreatedAt">创建时间（UTC 毫秒）。</param>
public sealed record GroupInviteLink(
    string Token,
    string ConversationId,
    long ExpiresAt,
    long? RevokedAt,
    long CreatedAt);

/// <summary>群邀请链接预览（C1c）：打开链接先确认目标群，再由用户明确加入。</summary>
/// <param name="ConversationId">目标群会话 ID。</param>
/// <param name="Title">群标题。</param>
/// <param name="MemberCount">当前成员数。</param>
/// <param name="MaxMembers">成员上限。</param>
/// <param name="CreatorDisplayName">创建者昵称。</param>
/// <param name="ExpiresAt">过期时间（UTC 毫秒）。</param>
/// <param name="Expired">是否已过期。</param>
/// <param name="Revoked">是否已撤销。</param>
/// <param name="AlreadyMember">当前用户是否已在群内。</param>
public sealed record GroupInvitePreview(
    string ConversationId,
    string Title,
    int MemberCount,
    int MaxMembers,
    string CreatorDisplayName,
    long ExpiresAt,
    bool Expired,
    bool Revoked,
    bool AlreadyMember);
