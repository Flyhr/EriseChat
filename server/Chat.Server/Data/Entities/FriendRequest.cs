using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// friend_requests：好友申请（F-17）。同一对用户在 pending 期间至多一条
/// （部分唯一索引 ux_friend_requests_pending），历史申请允许保留多条。
/// </summary>
public sealed class FriendRequest
{
    /// <summary>申请 ID。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>申请人用户 ID。</summary>
    public string FromUserId { get; set; } = string.Empty;

    /// <summary>被申请人用户 ID。</summary>
    public string ToUserId { get; set; } = string.Empty;

    /// <summary>附言（≤100 字符，可空）。</summary>
    public string? Message { get; set; }

    /// <summary>申请状态：pending / accepted / rejected / cancelled。</summary>
    public FriendRequestStatus Status { get; set; } = FriendRequestStatus.Pending;

    /// <summary>申请时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>处理时间（UTC 毫秒，pending 时为空）。</summary>
    public long? HandledAt { get; set; }
}
