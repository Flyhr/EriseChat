using System.Collections.Concurrent;

namespace Chat.Server.Realtime;

/// <summary>
/// typing 转发节流（M3-05 语义，M3-06 从 WebSocketConnectionHandler 抽出便于单测）：
/// 每个「连接 × 会话」在 1 秒窗口内只放行一次转发，超频静默丢弃（不回 error）。
/// 键数量超过阈值时清理 60 秒前的旧记录，避免长连接/多会话累积导致内存无限增长。
/// </summary>
public sealed class TypingThrottle
{
    /// <summary>节流窗口：同一连接同一会话 1 秒内最多转发一次。</summary>
    public const long WindowMilliseconds = 1_000;

    /// <summary>触发清理的键数量阈值。</summary>
    private const int CleanupThreshold = 5_000;

    /// <summary>清理时只移除超过该时长的旧记录。</summary>
    private const long StaleMilliseconds = 60_000;

    private readonly ConcurrentDictionary<(string ConnectionId, string ConversationId), long> _lastForwardAt = new();

    /// <summary>
    /// 判断本次 typing 是否放行；放行时记录时间。时间由调用方传入（毫秒），便于单测使用固定时钟。
    /// </summary>
    public bool TryPass(string connectionId, string conversationId, long nowMilliseconds)
    {
        var key = (connectionId, conversationId);
        if (_lastForwardAt.TryGetValue(key, out var lastForwardAt) &&
            nowMilliseconds - lastForwardAt < WindowMilliseconds)
        {
            return false;
        }

        _lastForwardAt[key] = nowMilliseconds;
        if (_lastForwardAt.Count > CleanupThreshold)
        {
            Cleanup(nowMilliseconds);
        }

        return true;
    }

    private void Cleanup(long nowMilliseconds)
    {
        var cutoff = nowMilliseconds - StaleMilliseconds;
        foreach (var pair in _lastForwardAt)
        {
            if (pair.Value < cutoff)
            {
                _lastForwardAt.TryRemove(pair.Key, out _);
            }
        }
    }
}
