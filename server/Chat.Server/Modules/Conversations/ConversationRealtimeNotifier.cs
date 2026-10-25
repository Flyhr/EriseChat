using Chat.Protocol.Frames;
using Chat.Server.Realtime;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Conversations;

/// <summary>
/// 会话变更的 WS 推送（M1-07）：新建/变更后向所有在线成员连接推一次完整 Conversation 对象（conv.updated）。
/// 推送为尽力而为：单条连接失败只记 debug，不影响 REST 主流程。
/// </summary>
public sealed class ConversationRealtimeNotifier
{
    private readonly ConnectionManager _connections;
    private readonly ILogger<ConversationRealtimeNotifier> _logger;

    public ConversationRealtimeNotifier(ConnectionManager connections, ILogger<ConversationRealtimeNotifier> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    /// <summary>向会话成员的在线连接推送 conv.updated（不阻塞调用方）。</summary>
    public void PushUpdated(IReadOnlyList<Conversation> conversations)
    {
        if (conversations.Count == 0)
        {
            return;
        }

        var frame = new ConvUpdatedFrame(conversations);
        var memberUserIds = conversations
            .SelectMany(conversation => conversation.Members)
            .Select(member => member.UserId)
            .Distinct(StringComparer.Ordinal);

        var pushed = 0;
        foreach (var userId in memberUserIds)
        {
            foreach (var connection in _connections.GetConnections(userId))
            {
                pushed++;
                _ = SendSafeAsync(connection, frame);
            }
        }

        _logger.LogInformation(
            "conv.updated 推送：{ConversationCount} 个会话 → {ConnectionCount} 条在线连接",
            conversations.Count,
            pushed);
    }

    private async Task SendSafeAsync(WsConnection connection, ConvUpdatedFrame frame)
    {
        try
        {
            await connection.SendFrameAsync(frame, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "conv.updated 推送失败：连接 {ConnectionId}", connection.ConnectionId);
        }
    }
}
