using Chat.Protocol.Frames;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Realtime;

/// <summary>
/// 服务端心跳（M1-06，可选实现）：每 25 秒向所有已认证连接发送 ping；
/// 客户端 ping 的 pong 应答在 WebSocketConnectionHandler 帧循环里即时完成。
/// 服务端不主动因无 pong 断线（客户端负责 30 秒无响应判断线并退避重连）。
/// </summary>
public sealed class HeartbeatService : BackgroundService
{
    /// <summary>心跳间隔（秒），与 hello 帧 heartbeatSec 一致。</summary>
    public const int HeartbeatSeconds = 25;

    private readonly ConnectionManager _connections;
    private readonly ILogger<HeartbeatService> _logger;

    public HeartbeatService(ConnectionManager connections, ILogger<HeartbeatService> logger)
    {
        _connections = connections;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(HeartbeatSeconds));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                var connections = _connections.GetAllConnections();
                if (connections.Count == 0)
                {
                    continue;
                }

                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                foreach (var connection in connections)
                {
                    _ = SendPingAsync(connection, now, stoppingToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 服务停止：正常退出。
        }
    }

    private async Task SendPingAsync(WsConnection connection, long timestamp, CancellationToken cancellationToken)
    {
        try
        {
            await connection.SendFrameAsync(new PingFrame(timestamp), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "服务端心跳发送失败（连接 {ConnectionId}）", connection.ConnectionId);
        }
    }
}
