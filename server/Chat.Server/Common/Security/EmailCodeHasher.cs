using System.Security.Cryptography;
using System.Text;
using Chat.Server.Data.Entities;

namespace Chat.Server.Common.Security;

/// <summary>
/// 邮箱验证码哈希：HMAC-SHA256(服务端密钥, email + purpose + code)，只存 Base64 哈希，不存明文。
/// 生成（M1-04）与校验（M1-05）共用同一实现，避免两处算法漂移。
/// </summary>
public static class EmailCodeHasher
{
    /// <summary>计算验证码哈希（密钥来自 secrets.json 的 Jwt:Key）。</summary>
    public static string Hash(string secretKey, string normalizedEmail, EmailCodePurpose purpose, string code)
    {
        if (string.IsNullOrWhiteSpace(secretKey))
        {
            throw new InvalidOperationException("JWT 密钥为空，无法计算验证码哈希（首次启动应已生成 secrets.json）");
        }

        var payload = $"{normalizedEmail}\n{purpose.ToString().ToLowerInvariant()}\n{code}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secretKey));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    /// <summary>恒定时间比较验证码哈希。</summary>
    public static bool Verify(string secretKey, string normalizedEmail, EmailCodePurpose purpose, string code, string storedHash)
    {
        var computed = Encoding.UTF8.GetBytes(Hash(secretKey, normalizedEmail, purpose, code));
        var stored = Encoding.UTF8.GetBytes(storedHash ?? string.Empty);
        return computed.Length == stored.Length && CryptographicOperations.FixedTimeEquals(computed, stored);
    }
}
