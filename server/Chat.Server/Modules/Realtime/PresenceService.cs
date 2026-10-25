using Chat.Protocol.Frames;
using Chat.Server.Data;
using Chat.Server.Realtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Realtime;

/// <summary>
/// 在线状态推送（M3-05，protocol/protocol.md §4.7）：用户连接数 0→1 时向「与其有共同会话的
/// 其他在线用户」推送 presence(userId, online=true)，1→0 时推送 online=false。
/// 判定由 WebSocketConnectionHandler 在连接生命周期内触发；本服务只负责查共同会话成员并推送。
/// 不落库、不查询离线状态；推送 fire-and-forget 风格（逐连接 try/catch 记 debug）。
/// </summary>
public sealed class PresenceService
{
    private readonly ConnectionManager _connections;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<PresenceService> _logger;

    public PresenceService(
        ConnectionManager connections,
        IServiceScopeFactory scopeFactory,
        ILogger<PresenceService> logger)
    {
        _connections = connections;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>上线：仅在调用方确认连接数由 0 变为 1 后调用。</summary>
    public Task OnConnectedAsync(string userId, CancellationToken cancellationToken) =>
        PushAsync(userId, online: true, cancellationToken);

    /// <summary>下线：仅在调用方确认该用户已无连接后调用。</summary>
    public Task OnDisconnectedAsync(string userId, CancellationToken cancellationToken) =>
        PushAsync(userId, online: false, cancellationToken);

    private async Task PushAsync(string userId, bool online, CancellationToken cancellationToken)
    {
        try
        {
            var peerUserIds = await FindPeerUserIdsAsync(userId, cancellationToken);
            if (peerUserIds.Count == 0)
            {
                return;
            }

            var frame = new PresenceFrame(userId, online);
            var sentConnections = 0;
            foreach (var peerUserId in peerUserIds)
            {
                foreach (var peerConnection in _connections.GetConnections(peerUserId))
                {
                    try
                    {
                        await peerConnection.SendFrameAsync(frame, CancellationToken.None);
                        sentConnections++;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "presence 推送失败：连接 {ConnectionId}", peerConnection.ConnectionId);
                    }
                }
            }

            _logger.LogInformation(
                "presence {State}：用户 {UserId} → {PeerCount} 个共同会话用户 / {ConnectionCount} 条在线连接",
                online ? "online" : "offline",
                userId,
                peerUserIds.Count,
                sentConnections);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "presence 推送异常：用户 {UserId}", userId);
        }
    }

    /// <summary>共同会话的其他成员（DISTINCT），即需要收到该用户在线状态的用户集合。</summary>
    private async Task<List<string>> FindPeerUserIdsAsync(string userId, CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        return await db.ConversationMembers.AsNoTracking()
            .Where(other => other.UserId != userId &&
                            db.ConversationMembers.Any(mine =>
                                mine.UserId == userId && mine.ConversationId == other.ConversationId))
            .Select(other => other.UserId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }
}
