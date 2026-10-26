namespace Chat.Protocol.Frames;

/// <summary>好友对象（protocol/protocol.md §4.9）。</summary>
/// <param name="UserId">好友用户 ID。</param>
/// <param name="DisplayName">好友昵称。</param>
/// <param name="Email">好友邮箱（内部版可直接下发）。</param>
/// <param name="CreatedAt">成为好友的时间（UTC 毫秒）。</param>
/// <param name="Remark">我对该好友的备注（空表示无备注）。</param>
/// <param name="GroupName">我对该好友的分组名（空表示未分组）。</param>
/// <param name="AvatarFileId">头像文件 ID（可选）。</param>
public sealed record Friend(
    string UserId,
    string DisplayName,
    string Email,
    long CreatedAt,
    string? Remark = null,
    string? GroupName = null,
    string? AvatarFileId = null);

/// <summary>好友申请对象（protocol/protocol.md §4.9）。</summary>
/// <param name="RequestId">申请 ID。</param>
/// <param name="FromUserId">申请人用户 ID。</param>
/// <param name="FromDisplayName">申请人昵称。</param>
/// <param name="ToUserId">被申请人用户 ID。</param>
/// <param name="ToDisplayName">被申请人昵称。</param>
/// <param name="Status">申请状态。</param>
/// <param name="CreatedAt">申请时间（UTC 毫秒）。</param>
/// <param name="Message">附言（≤100 字符，可空）。</param>
/// <param name="HandledAt">处理时间（UTC 毫秒，pending 时为空）。</param>
public sealed record FriendRequest(
    string RequestId,
    string FromUserId,
    string FromDisplayName,
    string ToUserId,
    string ToDisplayName,
    FriendRequestStatus Status,
    long CreatedAt,
    string? Message = null,
    long? HandledAt = null);
