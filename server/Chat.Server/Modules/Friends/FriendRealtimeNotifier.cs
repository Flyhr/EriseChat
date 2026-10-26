using Chat.Protocol.Frames;
using Chat.Server.Realtime;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Friends;

/// <summary>
/// 好友/申请变更的 WS 推送（F-17，protocol/protocol.md §4.9）：向目标用户的在线连接推
/// <c>friend.changed</c>。推送为尽力而为：单条连接失败只记 debug，不影响 REST 主流程。
/// </summary>
public sealed class FriendRealtimeNotifier
{
    private readonly ConnectionManager _connections;
    private readonly ILogger<FriendRealtimeNotifier> _logger;

    public FriendRealtimeNotifier(ConnectionManager connections, ILogger<FriendRealtimeNotifier> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    /// <summary>收到新的好友申请（推给被申请人）。</summary>
    public void PushRequestReceived(string toUserId, FriendRequest request) =>
        Push(toUserId, FriendChangedKinds.RequestReceived, friend: null, request, "request.received");

    /// <summary>自己发出的申请被接受（推给申请人，附好友快照）。</summary>
    public void PushRequestAccepted(string fromUserId, FriendRequest request, Friend friend) =>
        Push(fromUserId, FriendChangedKinds.RequestAccepted, friend, request, "request.accepted");

    /// <summary>自己发出的申请被拒绝（推给申请人）。</summary>
    public void PushRequestRejected(string fromUserId, FriendRequest request) =>
        Push(fromUserId, FriendChangedKinds.RequestRejected, friend: null, request, "request.rejected");

    /// <summary>对方撤销了发给自己的申请（推给被申请人）。</summary>
    public void PushRequestCancelled(string toUserId, FriendRequest request) =>
        Push(toUserId, FriendChangedKinds.RequestCancelled, friend: null, request, "request.cancelled");

    /// <summary>新增好友关系（推给指定一方）。</summary>
    public void PushFriendAdded(string userId, Friend friend) =>
        Push(userId, FriendChangedKinds.FriendAdded, friend, request: null, "friend.added");

    /// <summary>好友关系被删除（推给指定一方，附删除前快照）。</summary>
    public void PushFriendRemoved(string userId, Friend friend) =>
        Push(userId, FriendChangedKinds.FriendRemoved, friend, request: null, "friend.removed");

    /// <summary>备注/分组变更（推给修改者本人）。</summary>
    public void PushFriendUpdated(string userId, Friend friend) =>
        Push(userId, FriendChangedKinds.FriendUpdated, friend, request: null, "friend.updated");

    private void Push(string userId, string kind, Friend? friend, FriendRequest? request, string logKind)
    {
        var frame = new FriendChangedFrame(kind, friend, request);
        var pushed = 0;
        foreach (var connection in _connections.GetConnections(userId))
        {
            pushed++;
            _ = SendSafeAsync(connection, frame);
        }

        _logger.LogInformation(
            "friend.changed({Kind}) 推送：用户 {UserId} → {ConnectionCount} 条在线连接",
            logKind,
            userId,
            pushed);
    }

    private async Task SendSafeAsync(WsConnection connection, FrameEnvelope frame)
    {
        try
        {
            await connection.SendFrameAsync(frame, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "friend.changed 推送失败：连接 {ConnectionId}", connection.ConnectionId);
        }
    }
}
