using System.Security.Cryptography;
using System.Text;

namespace Chat.Server.Common.Security;

/// <summary>
/// 密码哈希：PBKDF2-HMAC-SHA256（随机 16 字节盐，210,000 次迭代 ≥ AGENTS §6 要求的 100,000）。
/// 存储格式：<c>pbkdf2-sha256${iterations}${saltBase64}${hashBase64}</c>。
/// </summary>
public static class PasswordHasher
{
    private const string Algorithm = "pbkdf2-sha256";
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 210_000;

    /// <summary>生成密码哈希（仅在注册时设置密码时调用）。</summary>
    public static string Hash(string password)
    {
        ArgumentNullException.ThrowIfNull(password);
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return $"{Algorithm}${Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    /// <summary>校验密码（恒定时间比较；格式非法/旧格式返回 false，不抛异常）。</summary>
    public static bool Verify(string password, string storedHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(storedHash))
        {
            return false;
        }

        var parts = storedHash.Split('$');
        if (parts.Length != 4 || !string.Equals(parts[0], Algorithm, StringComparison.Ordinal))
        {
            return false;
        }

        if (!int.TryParse(parts[1], out var iterations) || iterations < 100_000)
        {
            return false;
        }

        try
        {
            var salt = Convert.FromBase64String(parts[2]);
            var expected = Convert.FromBase64String(parts[3]);
            if (salt.Length < 8 || expected.Length < 16)
            {
                return false;
            }

            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>仅用于诊断/测试的算法标识。</summary>
    public static string AlgorithmName => Algorithm;

    /// <summary>仅用于诊断/测试的迭代次数。</summary>
    public static int IterationCount => Iterations;
}
