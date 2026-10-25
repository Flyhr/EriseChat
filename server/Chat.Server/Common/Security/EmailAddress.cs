using System.Net.Mail;

namespace Chat.Server.Common.Security;

/// <summary>邮箱规范化与格式校验（注册/登录/发码共用，保证同一邮箱在不同入口的写法一致）。</summary>
public static class EmailAddress
{
    /// <summary>去空格、转小写并校验格式；非法返回 false。</summary>
    public static bool TryNormalize(string? rawEmail, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawEmail))
        {
            return false;
        }

        var candidate = rawEmail.Trim().ToLowerInvariant();
        if (candidate.Length > 254)
        {
            return false;
        }

        if (!MailAddress.TryCreate(candidate, out var parsed) ||
            !string.Equals(parsed.Address, candidate, StringComparison.Ordinal))
        {
            return false;
        }

        normalized = candidate;
        return true;
    }
}
