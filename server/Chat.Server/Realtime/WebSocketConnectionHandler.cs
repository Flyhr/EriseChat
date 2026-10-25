using System.Net.WebSockets;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Realtime;

/// <summary>
/// WebSocket 连接处理器（M1-06）：10 秒内必须收到 auth 首帧；认证成功后回 hello、登记连接、
/// 踢掉同账号旧连接，然后进入帧循环（客户端 ping → pong；其它已知但未实现的帧忽略并记 debug）。
/// </summary>
public sealed class WebSocketConnectionHandler
{
    /// <summary>连接建立后必须在此时间内发送 auth 首帧。</summary>
    public const int AuthTimeoutMilliseconds = 10_000;

    private readonly ConnectionManager _connections;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<WebSocketConnectionHandler> _logger;

    public WebSocketConnectionHandler(
        ConnectionManager connections,
        IServiceScopeFactory scopeFactory,
        ILoggerFactory loggerFactory,
        ILogger<WebSocketConnectionHandler> logger)
    {
        _connections = connections;
        _scopeFactory = scopeFactory;
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
            connection.MarkAuthenticated(userId, auth.DeviceId!.Trim());
            _connections.Register(connection);

            // 单活动连接：把该账号已有连接先踢下线（先 kicked 再 close 4001）。
            await KickExistingAsync(connection, userId, connectionAborted);

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
                case AuthFrame:
                    _logger.LogDebug("WS 已认证连接重复发送 auth 帧，已忽略（连接 {ConnectionId}）", connection.ConnectionId);
                    break;
                default:
                    // message.send / sync.request 等业务帧在 M1-07+ 逐批接入；当前忽略但不断连。
                    _logger.LogDebug(
                        "WS 帧类型 {FrameType} 暂未实现，已忽略（连接 {ConnectionId}）",
                        parsed.Frame.Type,
                        connection.ConnectionId);
                    break;
            }
        }
    }

    private async Task KickExistingAsync(WsConnection newConnection, string userId, CancellationToken connectionAborted)
    {
        var existing = _connections.GetConnections(userId);
        foreach (var oldConnection in existing)
        {
            if (ReferenceEquals(oldConnection, newConnection))
            {
                continue;
            }

            _connections.Unregister(oldConnection);
            _logger.LogInformation(
                "单活动连接：踢下线旧连接 {OldConnectionId}（用户 {UserId}，新连接 {NewConnectionId}）",
                oldConnection.ConnectionId,
                userId,
                newConnection.ConnectionId);
            await oldConnection.KickAsync("账号在其他设备登录", connectionAborted);
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
