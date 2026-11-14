using Chat.Protocol.Frames;
using Chat.Server.Realtime;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Conversations;

/// <summary>
/// 会话变更的 WS 推送（M1-07 + M3-04）：新建/变更后向所有在线成员连接推一次完整 Conversation 对象
/// （conv.updated）；群聊 REST 操作产生的系统消息通过 message.new 广播给在线成员（含操作者本人，
/// 客户端按 messageId 去重）。推送为尽力而为：单条连接失败只记 debug，不影响 REST 主流程。
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

    /// <summary>
    /// 按收件人视角推送 conv.updated（F-14 群资料/成员变更）：每个成员收到的 Conversation 只反映自己的
    /// pinned / muted / lastReadSeq / unread，禁止把同一份会话广播给所有成员。不阻塞调用方。
    /// </summary>
    public void PushUpdatedPerRecipient(IReadOnlyDictionary<string, Conversation> conversationsByUserId)
    {
        if (conversationsByUserId.Count == 0)
        {
            return;
        }

        var pushed = 0;
        foreach (var (userId, conversation) in conversationsByUserId)
        {
            var frame = new ConvUpdatedFrame(new[] { conversation });
            foreach (var connection in _connections.GetConnections(userId))
            {
                pushed++;
                _ = SendSafeAsync(connection, frame);
            }
        }

        _logger.LogInformation(
            "conv.updated(按收件人) 推送：{RecipientCount} 个收件人 → {ConnectionCount} 条在线连接",
            conversationsByUserId.Count,
            pushed);
    }

    /// <summary>
    /// 向指定成员的在线连接广播 message.new（REST 场景含发送者本人；广播不带 clientMsgId，
    /// 客户端按 messageId 去重）。不阻塞调用方。
    /// </summary>
    public void PushNewMessage(Message message, IReadOnlyList<string> memberUserIds)
    {
        var frame = new MessageNewFrame(message with { ClientMsgId = null });
        var pushed = 0;
        foreach (var userId in memberUserIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var connection in _connections.GetConnections(userId))
            {
                pushed++;
                _ = SendSafeAsync(connection, frame);
            }
        }

        _logger.LogInformation(
            "message.new 推送：会话 {ConversationId}，seq {Seq} → {ConnectionCount} 条在线连接",
            message.ConversationId,
            message.Seq,
            pushed);
    }

    /// <summary>
    /// 向被移出/主动退出的用户推送 conv.updated（conversations 为空数组 + removedConversationIds），
    /// 客户端收到后移除该会话的本地状态（M3-04）。不阻塞调用方。
    /// </summary>
    public void PushRemoved(string userId, string conversationId)
    {
        var frame = new ConvUpdatedFrame(Array.Empty<Conversation>())
        {
            RemovedConversationIds = new[] { conversationId },
        };

        var pushed = 0;
        foreach (var connection in _connections.GetConnections(userId))
        {
            pushed++;
            _ = SendSafeAsync(connection, frame);
        }

        _logger.LogInformation(
            "conv.updated(removed) 推送：会话 {ConversationId}，用户 {UserId} → {ConnectionCount} 条在线连接",
            conversationId,
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
            _logger.LogDebug(exception, "conv.updated 推送失败：连接 {ConnectionId}", connection.ConnectionId);
        }
    }
}
