using System.Collections.Concurrent;

namespace Chat.Server.Realtime;

/// <summary>
/// 在线连接注册表：userId → 该账号当前连接集合（ConcurrentDictionary，单进程内存）。
/// 单活动连接策略由 WebSocketConnectionHandler 在认证成功时调用 KickExisting 实现；
/// M1-07+ 的消息广播从这里取连接。
/// </summary>
public sealed class ConnectionManager
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, WsConnection>> _connections =
        new(StringComparer.Ordinal);

    /// <summary>当前认证连接总数（健康检查/日志用）。</summary>
    public int Count => _connections.Values.Sum(bucket => bucket.Count);

    /// <summary>注册一条已认证连接。</summary>
    public void Register(WsConnection connection)
    {
        var userId = connection.UserId;
        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new InvalidOperationException("未认证的连接不能注册到 ConnectionManager");
        }

        var bucket = _connections.GetOrAdd(userId, _ => new ConcurrentDictionary<string, WsConnection>(StringComparer.Ordinal));
        bucket[connection.ConnectionId] = connection;
    }

    /// <summary>注销连接（连接结束时调用，可重复调用）。</summary>
    public void Unregister(WsConnection connection)
    {
        var userId = connection.UserId;
        if (string.IsNullOrWhiteSpace(userId) || !_connections.TryGetValue(userId, out var bucket))
        {
            return;
        }

        bucket.TryRemove(connection.ConnectionId, out _);
        if (bucket.IsEmpty)
        {
            _connections.TryRemove(userId, out _);
        }
    }

    /// <summary>取某账号当前连接快照（用于踢旧连接/广播）。</summary>
    public IReadOnlyList<WsConnection> GetConnections(string userId) =>
        _connections.TryGetValue(userId, out var bucket) ? bucket.Values.ToArray() : Array.Empty<WsConnection>();

    /// <summary>取全部已认证连接快照（心跳/服务端广播用）。</summary>
    public IReadOnlyList<WsConnection> GetAllConnections() =>
        _connections.Values.SelectMany(bucket => bucket.Values).ToArray();
}
