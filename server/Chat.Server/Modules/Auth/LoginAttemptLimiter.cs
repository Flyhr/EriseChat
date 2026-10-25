using Chat.Server.Common.Options;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Auth;

/// <summary>
/// 密码登录失败锁定（单进程内存：重启清零、多实例不共享；M1-05 交付说明已注明）。
/// 同一邮箱 5 分钟窗口内失败 5 次 → 锁定 5 分钟（阈值来自 RateLimit 配置）。
/// </summary>
public sealed class LoginAttemptLimiter
{
    private sealed class Entry
    {
        public int Failures;
        public long LastFailureAt;
        public long LockedUntil;
    }

    private const int CleanupThreshold = 10_000;

    private readonly object _sync = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly RateLimitOptions _options;

    public LoginAttemptLimiter(IOptions<RateLimitOptions> options)
    {
        _options = options.Value;
    }

    private int MaxAttempts => Math.Max(1, _options.LoginFailedMaxAttempts);

    private long LockMilliseconds => Math.Max(1, _options.LoginLockMinutes) * 60_000L;

    /// <summary>是否处于锁定中；返回剩余锁定时长（毫秒），0 表示未锁定。</summary>
    public long GetRemainingLockMilliseconds(string email, long nowUtcMilliseconds)
    {
        lock (_sync)
        {
            return _entries.TryGetValue(email, out var entry) && entry.LockedUntil > nowUtcMilliseconds
                ? entry.LockedUntil - nowUtcMilliseconds
                : 0;
        }
    }

    /// <summary>记录一次失败；达到阈值后进入锁定（返回锁定剩余毫秒，未锁定返回 0）。</summary>
    public long RecordFailure(string email, long nowUtcMilliseconds)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(email, out var entry))
            {
                entry = new Entry();
                _entries[email] = entry;
            }

            if (entry.LockedUntil > nowUtcMilliseconds)
            {
                return entry.LockedUntil - nowUtcMilliseconds;
            }

            // 失败窗口按锁定时长滚动：太久以前的失败不计入。
            if (nowUtcMilliseconds - entry.LastFailureAt > LockMilliseconds)
            {
                entry.Failures = 0;
            }

            entry.Failures++;
            entry.LastFailureAt = nowUtcMilliseconds;
            if (entry.Failures >= MaxAttempts)
            {
                entry.LockedUntil = nowUtcMilliseconds + LockMilliseconds;
                return entry.LockedUntil - nowUtcMilliseconds;
            }

            CleanupIfNeeded(nowUtcMilliseconds);
            return 0;
        }
    }

    /// <summary>登录成功后清除失败记录。</summary>
    public void Clear(string email)
    {
        lock (_sync)
        {
            _entries.Remove(email);
        }
    }

    private void CleanupIfNeeded(long nowUtcMilliseconds)
    {
        if (_entries.Count <= CleanupThreshold)
        {
            return;
        }

        foreach (var key in _entries.Keys.ToArray())
        {
            var entry = _entries[key];
            if (entry.LockedUntil <= nowUtcMilliseconds &&
                nowUtcMilliseconds - entry.LastFailureAt > LockMilliseconds)
            {
                _entries.Remove(key);
            }
        }
    }
}
