using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Conversations;
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
    private readonly ConversationService _conversations;
    private readonly ConversationRealtimeNotifier _notifier;
    private readonly ConnectionManager _connections;
    private readonly ILogger<MessageHub> _logger;

    public MessageHub(
        MessageService messages,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        ConnectionManager connections,
        ILogger<MessageHub> logger)
    {
        _messages = messages;
        _conversations = conversations;
        _notifier = notifier;
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

    /// <summary>处理 message.recall（F-18）：本人/窗口校验 → 落库撤回 → 向会话全部在线连接广播（含操作者）。</summary>
    public async Task HandleRecallAsync(
        WsConnection sender,
        MessageRecallFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(sender.UserId))
        {
            return;
        }

        try
        {
            var result = await _messages.RecallAsync(
                sender.UserId,
                request.ConversationId,
                request.MessageId,
                connectionAborted);
            var message = result.Message;
            var frame = new MessageRecalledFrame(
                message.ConversationId,
                message.MessageId,
                message.Seq,
                message.DeletedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sender.UserId);

            if (result.Changed)
            {
                var sent = await BroadcastToAllAsync(result.MemberUserIds, frame);
                _logger.LogInformation(
                    "message.recalled 广播：会话 {ConversationId} seq={Seq} messageId={MessageId} → {ConnectionCount} 条在线连接",
                    message.ConversationId,
                    message.Seq,
                    message.MessageId,
                    sent);

                // F-36：撤回的是置顶消息 → 先广播 message.recalled，再推按收件人视角的 conv.updated。
                if (result.PinnedClearedConversationId is not null)
                {
                    await PushPinnedMessageUpdatedAsync(
                        result.PinnedClearedConversationId,
                        result.MemberUserIds,
                        connectionAborted);
                }
            }
            else
            {
                // 幂等撤回：只向请求连接重发一条，deletedAt 不变、不重复广播（protocol §4.3 规则 3）。
                await sender.SendFrameAsync(frame, connectionAborted);
                _logger.LogInformation(
                    "message.recalled 幂等重发：会话 {ConversationId} seq={Seq} messageId={MessageId}（连接 {ConnectionId}）",
                    message.ConversationId,
                    message.Seq,
                    message.MessageId,
                    sender.ConnectionId);
            }
        }
        catch (ApiException exception)
        {
            _logger.LogInformation(
                "message.recall 校验失败：{Code}（连接 {ConnectionId}，会话 {ConversationId}）",
                exception.Code,
                sender.ConnectionId,
                request.ConversationId);
            await sender.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "message.recall 处理异常（连接 {ConnectionId}）", sender.ConnectionId);
            await sender.SendErrorAsync(
                ErrorCodes.InternalError,
                "服务端内部错误",
                request.ReqId,
                connectionAborted);
        }
    }

    /// <summary>处理 message.edit（F-18）：本人/仅文本/未撤回/窗口校验 → 落库编辑 → 向会话全部在线连接广播。</summary>
    public async Task HandleEditAsync(
        WsConnection sender,
        MessageEditFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(sender.UserId))
        {
            return;
        }

        try
        {
            var result = await _messages.EditAsync(
                sender.UserId,
                request.ConversationId,
                request.MessageId,
                request.Content,
                connectionAborted);
            var message = result.Message;
            var content = message.Content.IsString ? message.Content.GetString() ?? string.Empty : string.Empty;
            var frame = new MessageEditedFrame(
                message.ConversationId,
                message.MessageId,
                message.Seq,
                content,
                message.EditedAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                sender.UserId);
            var sent = await BroadcastToAllAsync(result.MemberUserIds, frame);
            _logger.LogInformation(
                "message.edited 广播：会话 {ConversationId} seq={Seq} messageId={MessageId} → {ConnectionCount} 条在线连接",
                message.ConversationId,
                message.Seq,
                message.MessageId,
                sent);
        }
        catch (ApiException exception)
        {
            _logger.LogInformation(
                "message.edit 校验失败：{Code}（连接 {ConnectionId}，会话 {ConversationId}）",
                exception.Code,
                sender.ConnectionId,
                request.ConversationId);
            await sender.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "message.edit 处理异常（连接 {ConnectionId}）", sender.ConnectionId);
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
    /// <summary>
    /// F-40：REST 转发落库成功后向目标会话全部在线连接广播 message.new。
    /// 与 WS 发送不同，这里无法识别「发起 REST 的连接」，因此包含转发者自己的其它在线连接（同消息去重由客户端按 messageId 处理）。
    /// </summary>
    public async Task BroadcastForwardedAsync(IReadOnlyList<string> memberUserIds, Chat.Protocol.Frames.Message message)
    {
        var frame = new MessageNewFrame(message with { ClientMsgId = null });
        var sent = await BroadcastToAllAsync(memberUserIds, frame);
        _logger.LogInformation(
            "message.new 广播（转发）：会话 {ConversationId} seq={Seq} messageId={MessageId} → {ConnectionCount} 条在线连接",
            message.ConversationId,
            message.Seq,
            message.MessageId,
            sent);
    }

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

                if (await TrySendAsync(connection, frame))
                {
                    sent++;
                }
            }
        }

        return sent;
    }

    /// <summary>向会话全部成员的所有在线连接发送帧（含操作者本人，F-18 撤回/编辑广播语义）。</summary>
    /// <summary>F-36：置顶消息被撤回后，按收件人视角重建会话并推送 conv.updated。</summary>
    private async Task PushPinnedMessageUpdatedAsync(
        string conversationId,
        IReadOnlyList<string> memberUserIds,
        CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetConversationEntityAsync(conversationId, cancellationToken);
        if (conversation is null)
        {
            return;
        }

        var perRecipient = await _conversations.BuildPerRecipientAsync(conversation, memberUserIds, cancellationToken);
        _notifier.PushUpdatedPerRecipient(perRecipient);
    }

    private async Task<int> BroadcastToAllAsync(IReadOnlyList<string> memberUserIds, FrameEnvelope frame)
    {
        var sent = 0;
        foreach (var userId in memberUserIds.Distinct(StringComparer.Ordinal))
        {
            foreach (var connection in _connections.GetConnections(userId))
            {
                if (await TrySendAsync(connection, frame))
                {
                    sent++;
                }
            }
        }

        return sent;
    }

    /// <summary>
    /// 向单个在线连接发送帧；失败只记录并跳过该连接，不中断本次广播。
    /// 消息已落库后再广播时，个别连接发送失败不应让 REST 请求返回失败（否则重试会命中幂等、无法补广播）。
    /// </summary>
    private async Task<bool> TrySendAsync(WsConnection connection, FrameEnvelope frame)
    {
        try
        {
            await connection.SendFrameAsync(frame, CancellationToken.None);
            return true;
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "向连接发送帧失败，已跳过该连接（ConnectionId={ConnectionId}）",
                connection.ConnectionId);
            return false;
        }
    }
}
