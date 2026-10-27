using System.Net.WebSockets;
using System.Text;
using Chat.Protocol.Frames;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Realtime;

/// <summary>
/// 一条 WS 连接的状态与发送封装：串行化发送（WebSocket 不允许并发 SendAsync）、
/// 统一「先发错误帧再 close」的关闭路径，并记录认证用户/设备。
/// </summary>
public sealed class WsConnection
{
    /// <summary>关闭原因在 WebSocket 协议里最多 123 字节（UTF-8），超出按字符安全截断。</summary>
    private const int MaxCloseReasonBytes = 123;

    private readonly WebSocket _socket;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private int _closed;

    public WsConnection(WebSocket socket, ILogger logger)
    {
        _socket = socket;
        _logger = logger;
    }

    /// <summary>连接 ID（日志排查用，不对外暴露）。</summary>
    public string ConnectionId { get; } = Guid.NewGuid().ToString("N");

    /// <summary>底层 WebSocket。</summary>
    public WebSocket Socket => _socket;

    /// <summary>认证后的用户 ID；null 表示尚未认证。</summary>
    public string? UserId { get; private set; }

    /// <summary>认证时上报的设备标识。</summary>
    public string? DeviceId { get; private set; }

    /// <summary>F-13：认证时对应的登录会话 ID（用于按会话远程下号）。</summary>
    public string? SessionId { get; private set; }

    /// <summary>建连时间（UTC 毫秒）。</summary>
    public long ConnectedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>是否已通过 auth 认证。</summary>
    public bool IsAuthenticated => UserId is not null;

    /// <summary>认证成功：记录用户、设备与登录会话。</summary>
    public void MarkAuthenticated(string userId, string deviceId, string? sessionId = null)
    {
        UserId = userId;
        DeviceId = deviceId;
        SessionId = sessionId;
    }

    /// <summary>发送一帧（JSON + 换行，串行化；已关闭/发送失败时静默返回）。</summary>
    public async Task SendFrameAsync(FrameEnvelope frame, CancellationToken cancellationToken)
    {
        var json = FrameCodec.Serialize(frame);
        await SendTextAsync(json, cancellationToken);
    }

    /// <summary>发送 error 帧。</summary>
    public Task SendErrorAsync(string code, string message, string? reqId, CancellationToken cancellationToken) =>
        SendFrameAsync(new ErrorFrame(code, message) { ReqId = reqId }, cancellationToken);

    /// <summary>发送一段已序列化 JSON（自动追加换行）。</summary>
    public async Task SendTextAsync(string json, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _closed) != 0)
        {
            return;
        }

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                return;
            }

            await FrameCodec.SendTextAsync(_socket, json, cancellationToken);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "WS 发送失败（连接 {ConnectionId}）", ConnectionId);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>踢下线：先发 kicked 帧，再以 4001 关闭（同一账号的新连接会调用）。</summary>
    public async Task KickAsync(string reason, CancellationToken cancellationToken)
    {
        await SendFrameAsync(new KickedFrame(reason), cancellationToken);
        await CloseAsync(WsCloseCodes.Kicked, reason, cancellationToken);
    }

    /// <summary>发送 close 帧（只执行一次；客户端已断开时忽略异常）。</summary>
    public async Task CloseAsync(int closeCode, string reason, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                await _socket.CloseAsync((WebSocketCloseStatus)closeCode, TruncateReason(reason), cancellationToken);
            }
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            _logger.LogDebug(exception, "WS 关闭失败（连接 {ConnectionId}）", ConnectionId);
        }
    }

    private static string TruncateReason(string reason)
    {
        if (string.IsNullOrEmpty(reason))
        {
            return string.Empty;
        }

        if (Encoding.UTF8.GetByteCount(reason) <= MaxCloseReasonBytes)
        {
            return reason;
        }

        var builder = new StringBuilder();
        var bytes = 0;
        foreach (var rune in reason.EnumerateRunes())
        {
            var runeBytes = rune.Utf8SequenceLength;
            if (bytes + runeBytes > MaxCloseReasonBytes)
            {
                break;
            }

            builder.Append(rune.ToString());
            bytes += runeBytes;
        }

        return builder.ToString();
    }
}
