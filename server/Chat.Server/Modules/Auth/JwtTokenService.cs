using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Chat.Server.Common.Options;
using Chat.Server.Data.Entities;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Server.Modules.Auth;

/// <summary>签发结果：token 字符串 + jti + 过期时间（UTC 毫秒，返回给前端）。</summary>
public sealed record IssuedToken(string AccessToken, string JwtId, long ExpiresAt);

/// <summary>
/// JWT 签发（HS256）：claims = sub(userId) / jti / role / email / exp（默认 7 天，可配置）。
/// 撤销由 sessions 表（jwt_id + revoked_at）控制，见 Program.cs 的 JwtBearer OnTokenValidated。
/// </summary>
public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _signingKey = new SymmetricSecurityKey(GetKeyBytes(_options.Key));
    }

    /// <summary>创建访问令牌（同时返回 jti，供 sessions.jwt_id 落库）。</summary>
    public IssuedToken CreateToken(User user, string jwtId, DateTimeOffset now)
    {
        var expiresAt = now.AddDays(Math.Max(1, _options.ExpireDays));
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Jti, jwtId),
            new("role", user.Role.ToString().ToLowerInvariant()),
            new(JwtRegisteredClaimNames.Email, user.Email),
        };

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            notBefore: now.UtcDateTime,
            expires: expiresAt.UtcDateTime,
            signingCredentials: new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256));

        return new IssuedToken(new JwtSecurityTokenHandler().WriteToken(token), jwtId, expiresAt.ToUnixTimeMilliseconds());
    }

    /// <summary>
    /// 解析密钥字节：secrets.json 中通常为 32 字节随机数的 Base64；
    /// 若人工配置了非 Base64 字符串则按 UTF-8 使用（不小于 32 字节由配置纪律保证）。
    /// </summary>
    public static byte[] GetKeyBytes(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("JWT 密钥为空（首次启动应已生成 secrets.json）");
        }

        try
        {
            var decoded = Convert.FromBase64String(key);
            if (decoded.Length >= 32)
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
            // 非 Base64 配置：按 UTF-8 处理。
        }

        var bytes = Encoding.UTF8.GetBytes(key);
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("JWT 密钥长度不足 32 字节，请重新生成 secrets.json");
        }

        return bytes;
    }
}
