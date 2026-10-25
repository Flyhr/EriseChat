using Chat.Server.Common.Options;
using Chat.Server.Modules.Auth;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Server.Realtime;

/// <summary>JWT 校验参数工厂（HTTP JwtBearer 与 WS auth 帧共用，避免两套校验规则漂移）。</summary>
public static class JwtTokenValidator
{
    /// <summary>按 Jwt 配置生成校验参数：校验签名/iss/aud/exp，时钟偏移取自配置；claims 保持原名（sub/jti/role）。</summary>
    public static TokenValidationParameters CreateTokenValidationParameters(JwtOptions options)
    {
        return new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = options.Issuer,
            ValidateAudience = true,
            ValidAudience = options.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(JwtTokenService.GetKeyBytes(options.Key)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(Math.Max(0, options.ClockSkewSeconds)),
            NameClaimType = "sub",
            RoleClaimType = "role",
        };
    }
}
