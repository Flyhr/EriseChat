using Chat.Server.Common.Options;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Auth;

/// <summary>验证码发送限流拒绝原因。</summary>
public enum EmailCodeRateLimitRejection
{
    /// <summary>允许发送。</summary>
    None,

    /// <summary>同邮箱发送间隔太短（默认 60 秒/次）。</summary>
    EmailTooFrequent,

    /// <summary>同邮箱 24 小时窗口内次数超限（默认 10 次/天）。</summary>
    EmailDailyExceeded,

    /// <summary>同 IP 1 小时窗口内次数超限（默认 20 次/小时）。</summary>
    IpHourlyExceeded,
}

/// <summary>
/// 验证码发送限流（单进程内存计数，重启清零；集群/多实例部署前需换成共享存储，见 M1-04 交付说明）。
/// 判定与记录在同一次加锁内完成，避免并发请求绕过限流。
/// </summary>
public sealed class EmailCodeRateLimiter
{
    private const long DayMilliseconds = 24L * 60 * 60 * 1000;
    private const long HourMilliseconds = 60L * 60 * 1000;
    private const int CleanupThreshold = 5000;

    private readonly object _sync = new();
    private readonly Dictionary<string, List<long>> _emailTimestamps = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<long>> _ipTimestamps = new(StringComparer.Ordinal);
    private readonly RateLimitOptions _options;

    public EmailCodeRateLimiter(IOptions<RateLimitOptions> options)
    {
        _options = options.Value;
    }

    /// <summary>原子地执行「检查 + 记录」：返回 None 才允许继续发送。</summary>
    public EmailCodeRateLimitRejection TryAcquire(string email, string? ip, long nowUtcMilliseconds)
    {
        lock (_sync)
        {
            var emailTimestamps = GetOrCreate(_emailTimestamps, email);
            Prune(emailTimestamps, nowUtcMilliseconds - DayMilliseconds);

            var intervalMilliseconds = Math.Max(0, _options.EmailCodePerEmailIntervalSeconds) * 1000L;
            if (intervalMilliseconds > 0 && emailTimestamps.Count > 0 &&
                nowUtcMilliseconds - emailTimestamps[^1] < intervalMilliseconds)
            {
                return EmailCodeRateLimitRejection.EmailTooFrequent;
            }

            if (_options.EmailCodePerEmailDaily > 0 && emailTimestamps.Count >= _options.EmailCodePerEmailDaily)
            {
                return EmailCodeRateLimitRejection.EmailDailyExceeded;
            }

            List<long>? ipTimestamps = null;
            if (!string.IsNullOrWhiteSpace(ip))
            {
                ipTimestamps = GetOrCreate(_ipTimestamps, ip);
                Prune(ipTimestamps, nowUtcMilliseconds - HourMilliseconds);
                if (_options.EmailCodePerIpHourly > 0 && ipTimestamps.Count >= _options.EmailCodePerIpHourly)
                {
                    return EmailCodeRateLimitRejection.IpHourlyExceeded;
                }
            }

            emailTimestamps.Add(nowUtcMilliseconds);
            ipTimestamps?.Add(nowUtcMilliseconds);
            CleanupIfNeeded(nowUtcMilliseconds);
            return EmailCodeRateLimitRejection.None;
        }
    }

    private static List<long> GetOrCreate(Dictionary<string, List<long>> store, string key)
    {
        if (!store.TryGetValue(key, out var timestamps))
        {
            timestamps = new List<long>(4);
            store[key] = timestamps;
        }

        return timestamps;
    }

    /// <summary>去掉时间窗口之外的旧记录（列表按时间升序追加）。</summary>
    private static void Prune(List<long> timestamps, long threshold)
    {
        var removeCount = 0;
        while (removeCount < timestamps.Count && timestamps[removeCount] < threshold)
        {
            removeCount++;
        }

        if (removeCount > 0)
        {
            timestamps.RemoveRange(0, removeCount);
        }
    }

    /// <summary>键数量过多时清理已过期条目，避免长期运行内存增长。</summary>
    private void CleanupIfNeeded(long nowUtcMilliseconds)
    {
        if (_emailTimestamps.Count <= CleanupThreshold)
        {
            return;
        }

        foreach (var key in _emailTimestamps.Keys.ToArray())
        {
            var timestamps = _emailTimestamps[key];
            Prune(timestamps, nowUtcMilliseconds - DayMilliseconds);
            if (timestamps.Count == 0)
            {
                _emailTimestamps.Remove(key);
            }
        }

        foreach (var key in _ipTimestamps.Keys.ToArray())
        {
            var timestamps = _ipTimestamps[key];
            Prune(timestamps, nowUtcMilliseconds - HourMilliseconds);
            if (timestamps.Count == 0)
            {
                _ipTimestamps.Remove(key);
            }
        }
    }
}
