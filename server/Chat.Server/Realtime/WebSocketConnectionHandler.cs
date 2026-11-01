using System.Net.WebSockets;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Modules.Conversations;
using Chat.Server.Modules.Messages;
using Chat.Server.Modules.Realtime;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chat.Server.Realtime;

/// <summary>
/// WebSocket 连接处理器（M1-06）：10 秒内必须收到 auth 首帧；认证成功后回 hello、登记连接；
/// 同账号连接数超过上限（F-22，默认 3）时踢最早建立的连接，然后进入帧循环（客户端 ping → pong；其它已知但未实现的帧忽略并记 debug）。
/// </summary>
public sealed class WebSocketConnectionHandler
{
    /// <summary>连接建立后必须在此时间内发送 auth 首帧。</summary>
    public const int AuthTimeoutMilliseconds = 10_000;

    private readonly ConnectionManager _connections;
    private readonly PresenceService _presence;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebSocketConnectionHandler> _logger;
    private readonly int _maxConnectionsPerUser;
    private readonly TypingThrottle _typingThrottle = new();
    public WebSocketConnectionHandler(
        ConnectionManager connections,
        PresenceService presence,
        IServiceScopeFactory scopeFactory,
        IOptions<AppOptions> appOptions,
        ILoggerFactory loggerFactory,
        ILogger<WebSocketConnectionHandler> logger)
    {
        _connections = connections;
        _presence = presence;
        _scopeFactory = scopeFactory;
        _maxConnectionsPerUser = Math.Clamp(appOptions.Value.MaxConnectionsPerUser, 1, 10);
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <summary>处理一条已升级的 WS 连接（返回时连接已关闭或对端断开）。</summary>
    public async Task HandleAsync(HttpContext context, WebSocket socket)
    {
        var connection = new WsConnection(socket, _loggerFactory.CreateLogger<WsConnection>());
        var connectionAborted = context.RequestAborted;
        _logger.LogDebug("WS 连接建立：{ConnectionId}（{Remote}）", connection.ConnectionId, context.Connection.RemoteIpAddress);

        try
        {
            var auth = await ReadAuthFrameAsync(connection, connectionAborted);
            if (auth is null)
            {
                return;
            }

            WsAuthResult authResult;
            using (var scope = _scopeFactory.CreateScope())
            {
                var authenticator = scope.ServiceProvider.GetRequiredService<WsAuthenticator>();
                authResult = await authenticator.ValidateAsync(
                    auth.Token,
                    auth.DeviceId,
                    auth.ProtocolVersion,
                    connectionAborted);
            }

            if (!authResult.Succeeded)
            {
                _logger.LogInformation(
                    "WS 认证失败：{ErrorCode}（连接 {ConnectionId}）",
                    authResult.ErrorCode,
                    connection.ConnectionId);
                await SendErrorAndCloseAsync(
                    connection,
                    authResult.ErrorCode ?? ErrorCodes.AuthFailed,
                    authResult.ErrorMessage ?? "认证失败",
                    auth.ReqId,
                    WsCloseCodes.ProtocolError,
                    "认证失败",
                    connectionAborted);
                return;
            }

            var userId = authResult.UserId!;
            // 记录注册前的在线状态，用于判定 presence 是否 0→1（单活动连接踢旧时不算重新上线）。
            var hadOnlineConnections = _connections.GetConnections(userId).Count > 0;
            connection.MarkAuthenticated(userId, auth.DeviceId!.Trim(), authResult.SessionId);
            _connections.Register(connection);

            // 多端在线（F-22）：连接数超过上限时，只踢「最早建立」的超额连接（先 kicked 再 close 4001）。
            await KickOverLimitAsync(connection, userId, connectionAborted);

            var hello = new HelloFrame(
                userId,
                HeartbeatService.HeartbeatSeconds,
                DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Chat.Protocol.ProtocolVersion.Current);
            await connection.SendFrameAsync(hello, connectionAborted);

            _logger.LogInformation(
                "WS 认证成功：用户 {UserId}，设备 {DeviceId}，连接 {ConnectionId}",
                userId,
                connection.DeviceId,
                connection.ConnectionId);

            if (!hadOnlineConnections)
            {
                await _presence.OnConnectedAsync(userId, connectionAborted);
            }

            await RunMessageLoopAsync(connection, connectionAborted);
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("WS 连接取消：{ConnectionId}", connection.ConnectionId);
        }
        catch (System.Net.WebSockets.WebSocketException exception)
        {
            _logger.LogDebug(exception, "WS 连接异常断开：{ConnectionId}", connection.ConnectionId);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "WS 连接处理异常：{ConnectionId}", connection.ConnectionId);
        }
        finally
        {
            _connections.Unregister(connection);
            _logger.LogDebug("WS 连接结束：{ConnectionId}（用户 {UserId}）", connection.ConnectionId, connection.UserId);

            // presence 下线：仅当该用户已无任何在线连接时推送（被踢旧连接此时新连接仍在，不推）。
            if (connection.UserId is { Length: > 0 } disconnectedUserId &&
                _connections.GetConnections(disconnectedUserId).Count == 0)
            {
                try
                {
                    await _presence.OnDisconnectedAsync(disconnectedUserId, CancellationToken.None);
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(exception, "presence 下线推送失败：用户 {UserId}", disconnectedUserId);
                }
            }
        }
    }

    /// <summary>读取认证首帧：10 秒内必须是 auth；二进制帧忽略、超限关闭 1009、非法 JSON 关闭 4002。</summary>
    private async Task<AuthFrame?> ReadAuthFrameAsync(WsConnection connection, CancellationToken connectionAborted)
    {
        while (true)
        {
            // 认证超时用 Task.WhenAny 判定：直接取消 ReceiveAsync 的 token 会让 Kestrel 中止整条连接，
            // 导致客户端收不到 AUTH_REQUIRED 错误帧（只能看到异常关闭 1006）。
            var receiveTask = FrameCodec.ReceiveAsync(connection.Socket, connectionAborted);
            var timeoutTask = Task.Delay(AuthTimeoutMilliseconds, connectionAborted);
            var completed = await Task.WhenAny(receiveTask, timeoutTask);
            if (completed == timeoutTask && !connectionAborted.IsCancellationRequested)
            {
                _logger.LogInformation("WS 认证超时（10 秒内未发送 auth 帧）：{ConnectionId}", connection.ConnectionId);
                await SendErrorAndCloseAsync(
                    connection,
                    ErrorCodes.AuthRequired,
                    "连接建立后 10 秒内必须发送 auth 帧",
                    null,
                    WsCloseCodes.ProtocolError,
                    "认证超时",
                    connectionAborted);
                // 关闭后接收任务会以异常结束：观察掉它，避免未观察异常。
                try
                {
                    await receiveTask;
                }
                catch
                {
                    // 已主动关闭，忽略。
                }

                return null;
            }

            var received = await receiveTask;

            switch (received.Kind)
            {
                case FrameReceiveKind.Closed:
                    return null;
                case FrameReceiveKind.TooLarge:
                    await CloseForTooLargeAsync(connection, connectionAborted);
                    return null;
                case FrameReceiveKind.BinaryIgnored:
                    _logger.LogDebug("认证前收到二进制帧，已忽略（连接 {ConnectionId}）", connection.ConnectionId);
                    continue;
            }

            var parsed = FrameCodec.Parse(received.Json ?? string.Empty);
            if (parsed.Status == FrameParseStatus.Invalid)
            {
                await CloseForBadFrameAsync(connection, null, connectionAborted);
                return null;
            }

            if (parsed.Frame is not AuthFrame auth)
            {
                await SendErrorAndCloseAsync(
                    connection,
                    ErrorCodes.AuthRequired,
                    "认证前必须发送 auth 帧",
                    parsed.Frame?.ReqId,
                    WsCloseCodes.ProtocolError,
                    "认证前必须发送 auth 帧",
                    connectionAborted);
                return null;
            }

            return auth;
        }
    }

    /// <summary>认证后的帧循环：ping→pong；未知/未实现帧忽略并记 debug（不断连）。</summary>
    private async Task RunMessageLoopAsync(WsConnection connection, CancellationToken connectionAborted)
    {
        while (!connectionAborted.IsCancellationRequested)
        {
            var received = await FrameCodec.ReceiveAsync(connection.Socket, connectionAborted);

            switch (received.Kind)
            {
                case FrameReceiveKind.Closed:
                    _logger.LogDebug(
                        "WS 客户端关闭连接：{ConnectionId}（{Status} {Description}）",
                        connection.ConnectionId,
                        received.CloseStatus,
                        received.CloseDescription);
                    // 客户端发起 close：回 close 完成握手（已关闭时静默忽略）。
                    await connection.CloseAsync(WsCloseCodes.Normal, "bye", CancellationToken.None);
                    return;
                case FrameReceiveKind.BinaryIgnored:
                    _logger.LogDebug("WS 收到二进制帧，已忽略（连接 {ConnectionId}）", connection.ConnectionId);
                    continue;
                case FrameReceiveKind.TooLarge:
                    await CloseForTooLargeAsync(connection, connectionAborted);
                    return;
            }

            var parsed = FrameCodec.Parse(received.Json ?? string.Empty);
            if (parsed.Status == FrameParseStatus.Invalid)
            {
                await CloseForBadFrameAsync(connection, null, connectionAborted);
                return;
            }

            if (parsed.Status == FrameParseStatus.UnknownType || parsed.Frame is null)
            {
                _logger.LogDebug("WS 收到未知帧类型，已忽略（连接 {ConnectionId}）", connection.ConnectionId);
                continue;
            }

            switch (parsed.Frame)
            {
                case PingFrame ping:
                    await connection.SendFrameAsync(new PongFrame(ping.Ts), connectionAborted);
                    break;
                case ConvListRequestFrame convListRequest:
                    await HandleConvListAsync(connection, convListRequest, connectionAborted);
                    break;
                case MessageSendFrame messageSend:
                    await HandleMessageSendAsync(connection, messageSend, connectionAborted);
                    break;
                case MessageRecallFrame messageRecall:
                    await HandleMessageRecallAsync(connection, messageRecall, connectionAborted);
                    break;
                case MessageEditFrame messageEdit:
                    await HandleMessageEditAsync(connection, messageEdit, connectionAborted);
                    break;
                case SyncRequestFrame syncRequest:
                    await HandleSyncAsync(connection, syncRequest, connectionAborted);
                    break;
                case ReadUpdateFrame readUpdate:
                    await HandleReadUpdateAsync(connection, readUpdate, connectionAborted);
                    break;
                case TypingClientFrame typing:
                    await HandleTypingAsync(connection, typing, connectionAborted);
                    break;
                case AuthFrame:
                    _logger.LogDebug("WS 已认证连接重复发送 auth 帧，已忽略（连接 {ConnectionId}）", connection.ConnectionId);
                    break;
                default:
                    // presence 为服务端推送帧（C→S 方向按「忽略并记 debug」处理，protocol §4.7）；未知帧不断连。
                    _logger.LogDebug(
                        "WS 帧类型 {FrameType} 暂未实现，已忽略（连接 {ConnectionId}）",
                        parsed.Frame.Type,
                        connection.ConnectionId);
                    break;
            }
        }
    }

    /// <summary>conv.list（M1-07）：按当前连接用户返回会话列表，reqId 原样回传。</summary>
    private async Task HandleConvListAsync(
        WsConnection connection,
        ConvListRequestFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(connection.UserId))
        {
            return;
        }

        using var scope = _scopeFactory.CreateScope();
        var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
        var list = await conversations.ListAsync(connection.UserId, connectionAborted);
        await connection.SendFrameAsync(new ConvListResponseFrame(list) { ReqId = request.ReqId }, connectionAborted);
        _logger.LogDebug(
            "WS conv.list 返回 {ConversationCount} 个会话（连接 {ConnectionId}，reqId {ReqId}）",
            list.Count,
            connection.ConnectionId,
            request.ReqId);
    }

    /// <summary>message.send（M1-08）：落库/幂等回执/广播由 MessageHub 处理；校验失败只回 error，不断连。</summary>
    private async Task HandleMessageSendAsync(
        WsConnection connection,
        MessageSendFrame request,
        CancellationToken connectionAborted)
    {
        using var scope = _scopeFactory.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<MessageHub>();
        await hub.HandleSendAsync(connection, request, connectionAborted);
    }

    /// <summary>message.recall（F-18）：撤回/幂等/广播由 MessageHub 处理；校验失败只回 error，不断连。</summary>
    private async Task HandleMessageRecallAsync(
        WsConnection connection,
        MessageRecallFrame request,
        CancellationToken connectionAborted)
    {
        using var scope = _scopeFactory.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<MessageHub>();
        await hub.HandleRecallAsync(connection, request, connectionAborted);
    }

    /// <summary>message.edit（F-18）：编辑/广播由 MessageHub 处理；校验失败只回 error，不断连。</summary>
    private async Task HandleMessageEditAsync(
        WsConnection connection,
        MessageEditFrame request,
        CancellationToken connectionAborted)
    {
        using var scope = _scopeFactory.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<MessageHub>();
        await hub.HandleEditAsync(connection, request, connectionAborted);
    }

    /// <summary>sync.request（M1-08）：只读补拉，reqId 原样回传。</summary>
    private async Task HandleSyncAsync(
        WsConnection connection,
        SyncRequestFrame request,
        CancellationToken connectionAborted)
    {
        using var scope = _scopeFactory.CreateScope();
        var hub = scope.ServiceProvider.GetRequiredService<MessageHub>();
        await hub.HandleSyncAsync(connection, request, connectionAborted);
    }

    /// <summary>read.update（M3-05）：只增不减更新自己的已读游标；单聊推 read.updated 给对方（不回显）。</summary>
    private async Task HandleReadUpdateAsync(
        WsConnection connection,
        ReadUpdateFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(connection.UserId))
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var reads = scope.ServiceProvider.GetRequiredService<ReadService>();
            var result = await reads.UpdateReadAsync(connection.UserId, request.ConversationId, request.LastReadSeq, connectionAborted);

            // 只增不减：游标未推进（重复/更小的上报）不广播，避免对方收到无变化的 read.updated。
            if (result.Changed && !string.IsNullOrEmpty(result.OtherUserId))
            {
                var frame = new ReadUpdatedFrame(request.ConversationId, connection.UserId, result.LastReadSeq);
                foreach (var peer in _connections.GetConnections(result.OtherUserId))
                {
                    try
                    {
                        await peer.SendFrameAsync(frame, CancellationToken.None);
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "read.updated 推送失败：连接 {ConnectionId}", peer.ConnectionId);
                    }
                }
            }

            _logger.LogInformation(
                "read.update：会话 {ConversationId}，用户 {UserId}，lastReadSeq {LastReadSeq}（连接 {ConnectionId}）",
                request.ConversationId,
                connection.UserId,
                result.LastReadSeq,
                connection.ConnectionId);
        }
        catch (ApiException exception)
        {
            await connection.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "read.update 处理异常（连接 {ConnectionId}）", connection.ConnectionId);
            await connection.SendErrorAsync(ErrorCodes.InternalError, "服务端内部错误", request.ReqId, connectionAborted);
        }
    }

    /// <summary>typing（M3-05）：成员校验 → 1 秒节流 → 转发给会话内其他在线成员（跳过发送连接）。</summary>
    private async Task HandleTypingAsync(
        WsConnection connection,
        TypingClientFrame request,
        CancellationToken connectionAborted)
    {
        if (string.IsNullOrWhiteSpace(connection.UserId))
        {
            return;
        }

        try
        {
            IReadOnlyList<string> memberUserIds;
            using (var scope = _scopeFactory.CreateScope())
            {
                var conversations = scope.ServiceProvider.GetRequiredService<ConversationService>();
                memberUserIds = await conversations.GetMemberUserIdsAsync(connection.UserId, request.ConversationId, connectionAborted);
            }

            // 节流：每连接 × 每会话 1 秒（M3-05 语义；M3-06 抽到 TypingThrottle 便于单测）。
            if (!_typingThrottle.TryPass(connection.ConnectionId, request.ConversationId, Environment.TickCount64))
            {
                return;
            }

            var frame = new TypingServerFrame(request.ConversationId, connection.UserId);
            var forwarded = 0;
            foreach (var memberId in memberUserIds)
            {
                if (string.Equals(memberId, connection.UserId, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var peer in _connections.GetConnections(memberId))
                {
                    if (ReferenceEquals(peer, connection))
                    {
                        continue;
                    }

                    try
                    {
                        await peer.SendFrameAsync(frame, CancellationToken.None);
                        forwarded++;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogDebug(exception, "typing 推送失败：连接 {ConnectionId}", peer.ConnectionId);
                    }
                }
            }

            _logger.LogDebug(
                "typing 转发：会话 {ConversationId}，用户 {UserId} → {Forwarded} 条连接（连接 {ConnectionId}）",
                request.ConversationId,
                connection.UserId,
                forwarded,
                connection.ConnectionId);
        }
        catch (ApiException exception)
        {
            await connection.SendErrorAsync(exception.Code, exception.Message, request.ReqId, connectionAborted);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "typing 处理异常（连接 {ConnectionId}）", connection.ConnectionId);
            await connection.SendErrorAsync(ErrorCodes.InternalError, "服务端内部错误", request.ReqId, connectionAborted);
        }
    }

    private async Task KickOverLimitAsync(WsConnection newConnection, string userId, CancellationToken connectionAborted)
    {
        // 注：newConnection 已登记，existing 包含它；只有总数超过上限时才踢，且按 ConnectedAt 升序取最早的超额连接。
        var existing = _connections.GetConnections(userId);
        if (existing.Count <= _maxConnectionsPerUser)
        {
            return;
        }

        var overflow = existing
            .Where(oldConnection => !ReferenceEquals(oldConnection, newConnection))
            .OrderBy(oldConnection => oldConnection.ConnectedAt)
            .ThenBy(oldConnection => oldConnection.ConnectionId, StringComparer.Ordinal)
            .Take(existing.Count - _maxConnectionsPerUser)
            .ToArray();

        foreach (var oldConnection in overflow)
        {
            _connections.Unregister(oldConnection);
            _logger.LogInformation(
                "多端在线：账号连接数超过上限 {Limit}，踢下线最早连接 {OldConnectionId}（用户 {UserId}，新连接 {NewConnectionId}）",
                _maxConnectionsPerUser,
                oldConnection.ConnectionId,
                userId,
                newConnection.ConnectionId);
            await oldConnection.KickAsync($"账号连接数超出上限（最多 {_maxConnectionsPerUser} 端同时在线）", connectionAborted);
        }
    }

    private async Task CloseForTooLargeAsync(WsConnection connection, CancellationToken cancellationToken)
    {
        _logger.LogInformation("WS 帧超过 256 KiB，关闭 1009：{ConnectionId}", connection.ConnectionId);
        await SendErrorAndCloseAsync(
            connection,
            ErrorCodes.MessageTooLarge,
            "帧超过 256 KiB 上限",
            null,
            WsCloseCodes.MessageTooBig,
            "帧过大",
            cancellationToken);
    }

    private async Task CloseForBadFrameAsync(WsConnection connection, string? reqId, CancellationToken cancellationToken)
    {
        _logger.LogInformation("WS 帧格式非法，关闭 4002：{ConnectionId}", connection.ConnectionId);
        await SendErrorAndCloseAsync(
            connection,
            ErrorCodes.BadFrame,
            "帧格式非法（非法 JSON 或缺少 type）",
            reqId,
            WsCloseCodes.ProtocolError,
            "BAD_FRAME",
            cancellationToken);
    }

    private static async Task SendErrorAndCloseAsync(
        WsConnection connection,
        string code,
        string message,
        string? reqId,
        int closeCode,
        string closeReason,
        CancellationToken cancellationToken)
    {
        await connection.SendErrorAsync(code, message, reqId, cancellationToken);
        await connection.CloseAsync(closeCode, closeReason, cancellationToken);
    }
}
