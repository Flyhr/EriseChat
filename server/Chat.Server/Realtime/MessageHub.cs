using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Messages;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Realtime;

/// <summary>
/// 消息相关 WS 帧处理（M1-08，protocol/protocol.md §4.3/§4.4）：
/// message.send → 落库后回 ack（仅发送连接）并向其它在线成员广播 message.new（不回发送连接）；
/// sync.request → 回 sync.response（回传 reqId）。校验失败只回 error 帧，不断连。
/// </summary>
public sealed class MessageHub
{
    private readonly MessageService _messages;
    private readonly ConnectionManager _connections;
    private readonly ILogger<MessageHub> _logger;

    public MessageHub(MessageService messages, ConnectionManager connections, ILogger<MessageHub> logger)
    {
        _messages = messages;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>处理 message.send：落库/幂等回执 → ack → 广播 message.new。</summary>
    public async Task HandleSendAsync(
        WsConnection sender,
        MessageSendFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(sender.UserId))
        {
            return;
        }

        try
        {
            var result = await _messages.SendAsync(sender.UserId, request, connectionAborted);
            var message = result.Message;

            var ack = new MessageAckFrame(
                message.ClientMsgId ?? request.ClientMsgId,
                message.MessageId,
                message.ConversationId,
                message.Seq,
                message.CreatedAt)
            {
                ReqId = request.ReqId,
                Reply = message.Reply,
            };
            await sender.SendFrameAsync(ack, connectionAborted);
            _logger.LogInformation(
                "message.ack：会话 {ConversationId} seq={Seq} messageId={MessageId}（连接 {ConnectionId}，新落库={Created}）",
                message.ConversationId,
                message.Seq,
                message.MessageId,
                sender.ConnectionId,
                result.Created);

            if (!result.Created)
            {
                // 幂等重发：已广播过，不重复广播。
                return;
            }

            // 广播给会话内除发送连接外的成员（protocol/protocol.md §4.3：广播可不带 clientMsgId）。
            var broadcast = new MessageNewFrame(message with { ClientMsgId = null });
            var sent = await BroadcastAsync(result.MemberUserIds, sender, broadcast);
            _logger.LogInformation(
                "message.new 广播：会话 {ConversationId} seq={Seq} → {ConnectionCount} 条在线连接",
                message.ConversationId,
                message.Seq,
                sent);
        }
        catch (ApiException exception)
        {
            _logger.LogInformation(
                "message.send 校验失败：{Code}（连接 {ConnectionId}，会话 {ConversationId}）",
                exception.Code,
                sender.ConnectionId,
                request.ConversationId);
            await sender.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "message.send 处理异常（连接 {ConnectionId}）", sender.ConnectionId);
            await sender.SendErrorAsync(
                ErrorCodes.InternalError,
                "服务端内部错误",
                request.ReqId,
                connectionAborted);
        }
    }

    /// <summary>处理 sync.request：只读补拉，回传 reqId。</summary>
    public async Task HandleSyncAsync(
        WsConnection sender,
        SyncRequestFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(sender.UserId))
        {
            return;
        }

        try
        {
            var result = await _messages.SyncAsync(sender.UserId, request, connectionAborted);
            await sender.SendFrameAsync(
                new SyncResponseFrame(result.Batches, result.HasMore) { ReqId = request.ReqId },
                connectionAborted);

            var messageCount = result.Batches.Sum(batch => batch.Messages.Count);
            _logger.LogInformation(
                "sync.response：{BatchCount} 个会话 / {MessageCount} 条消息 / hasMore={HasMore}（连接 {ConnectionId}）",
                result.Batches.Count,
                messageCount,
                result.HasMore,
                sender.ConnectionId);
        }
        catch (ApiException exception)
        {
            _logger.LogInformation(
                "sync.request 校验失败：{Code}（连接 {ConnectionId}）",
                exception.Code,
                sender.ConnectionId);
            await sender.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "sync.request 处理异常（连接 {ConnectionId}）", sender.ConnectionId);
            await sender.SendErrorAsync(
                ErrorCodes.InternalError,
                "服务端内部错误",
                request.ReqId,
                connectionAborted);
        }
    }

    /// <summary>向会话内其它在线连接发送 message.new（跳过发送连接本身）。</summary>
    private async Task<int> BroadcastAsync(
        IReadOnlyList<string> memberUserIds,
        WsConnection sender,
        MessageNewFrame frame)
    {
        var sent = 0;
        foreach (var userId in memberUserIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var connection in _connections.GetConnections(userId))
            {
                if (ReferenceEquals(connection, sender))
                {
                    continue;
                }

                await connection.SendFrameAsync(frame, CancellationToken.None);
                sent++;
            }
        }

        return sent;
    }
}
