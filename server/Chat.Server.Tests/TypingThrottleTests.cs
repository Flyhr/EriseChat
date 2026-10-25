using Chat.Server.Realtime;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：typing 转发节流单测（M3-05 语义：每连接 × 每会话 1 秒窗口；超频静默丢弃）。
/// </summary>
public sealed class TypingThrottleTests
{
    private const long Now = 1_700_000_000_000;

    [Fact]
    public void TryPass_FirstAttempt_Passes()
    {
        var throttle = new TypingThrottle();

        Assert.True(throttle.TryPass("conn-1", "conv-1", Now));
    }

    [Fact]
    public void TryPass_WithinWindow_IsRejected()
    {
        var throttle = new TypingThrottle();

        Assert.True(throttle.TryPass("conn-1", "conv-1", Now));
        Assert.False(throttle.TryPass("conn-1", "conv-1", Now + TypingThrottle.WindowMilliseconds - 500));
    }

    [Fact]
    public void TryPass_AfterWindow_PassesAgain()
    {
        var throttle = new TypingThrottle();

        Assert.True(throttle.TryPass("conn-1", "conv-1", Now));
        Assert.True(throttle.TryPass("conn-1", "conv-1", Now + TypingThrottle.WindowMilliseconds + 1));
    }

    [Fact]
    public void TryPass_DifferentConversationOrConnection_IsIndependent()
    {
        var throttle = new TypingThrottle();

        Assert.True(throttle.TryPass("conn-1", "conv-1", Now));

        // 同一连接不同会话、不同连接同一会话：各自独立放行。
        Assert.True(throttle.TryPass("conn-1", "conv-2", Now));
        Assert.True(throttle.TryPass("conn-2", "conv-1", Now));

        // 各自窗口内重复发送仍被拒绝。
        Assert.False(throttle.TryPass("conn-1", "conv-1", Now + 100));
        Assert.False(throttle.TryPass("conn-2", "conv-1", Now + 100));
    }

    [Fact]
    public void TryPass_CleansStaleEntries_WhenOverThreshold()
    {
        var throttle = new TypingThrottle();
        var stale = Now - 10 * 60_000;

        // 超过 5000 个旧键（60 秒前），触发清理逻辑。
        for (var i = 0; i < 5_001; i++)
        {
            Assert.True(throttle.TryPass($"old-{i:D4}", "conv", stale));
        }

        // 新键插入后触发清理：旧键被移除，缓存不会无限增长。
        Assert.True(throttle.TryPass("fresh", "conv", Now));

        // 已被清理的旧键可以重新放行（证明确实被移除）。
        Assert.True(throttle.TryPass("old-0000", "conv", Now));
    }
}
