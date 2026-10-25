using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Chat.Protocol;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Chat.Server.Realtime;

/// <summary>WS auth 帧校验结果（失败时 ErrorCode 为 protocol/protocol.md §5.1 的错误码）。</summary>
public sealed record WsAuthResult(bool Succeeded, string? UserId, string? ErrorCode, string? ErrorMessage)
{
    public static WsAuthResult Success(string userId) => new(true, userId, null, null);

    public static WsAuthResult Fail(string errorCode, string errorMessage) => new(false, null, errorCode, errorMessage);
}

/// <summary>
/// WS 认证（M1-06）：校验 auth 帧的 protocolVersion/token/deviceId，并确认 JWT 签名有效、
/// sessions 未撤销、users.status=active（与 REST JwtBearer 的 OnTokenValidated 一致）。
/// </summary>
public sealed class WsAuthenticator
{
    private const int MaxDeviceIdLength = 128;

    private readonly ChatDbContext _db;
    private readonly TokenValidationParameters _validationParameters;
    private readonly ILogger<WsAuthenticator> _logger;

    // JwtSecurityTokenHandler 默认把 sub 等入站 claim 映射成 MS 长 URI（MapInboundClaims=true），
    // 会导致 FindFirst("sub") 取不到值；这里与 JwtBearer 的 MapInboundClaims=false 保持一致。
    private static readonly JwtSecurityTokenHandler TokenHandler = new() { MapInboundClaims = false };

    public WsAuthenticator(ChatDbContext db, IOptions<JwtOptions> jwtOptions, ILogger<WsAuthenticator> logger)
    {
        _db = db;
        _validationParameters = JwtTokenValidator.CreateTokenValidationParameters(jwtOptions.Value);
        _logger = logger;
    }

    /// <summary>校验 auth 帧字段；成功返回 userId。</summary>
    public async Task<WsAuthResult> ValidateAsync(
        string? token,
        string? deviceId,
        int? protocolVersion,
        CancellationToken cancellationToken)
    {
        if (protocolVersion is not null && protocolVersion != ProtocolVersion.Current)
        {
            return WsAuthResult.Fail(
                ErrorCodes.ProtocolVersionMismatch,
                $"协议版本不兼容（服务端 {ProtocolVersion.Current}，客户端 {protocolVersion}）");
        }

        if (string.IsNullOrWhiteSpace(token))
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "auth 帧缺少 token");
        }

        var normalizedDeviceId = (deviceId ?? string.Empty).Trim();
        if (normalizedDeviceId.Length == 0)
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "auth 帧缺少 deviceId");
        }

        if (normalizedDeviceId.Length > MaxDeviceIdLength)
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, $"deviceId 过长（最多 {MaxDeviceIdLength} 个字符）");
        }

        ClaimsPrincipal principal;
        try
        {
            principal = TokenHandler.ValidateToken(token, _validationParameters, out _);
        }
        catch (SecurityTokenExpiredException)
        {
            _logger.LogInformation("WS auth 令牌已过期");
            return WsAuthResult.Fail(ErrorCodes.AuthExpired, "登录已过期，请重新登录");
        }
        catch (SecurityTokenException exception)
        {
            // 只记录异常类型与 IDX 错误号，绝不输出 token 内容。
            _logger.LogInformation("WS auth JWT 校验失败：{ExceptionType}", exception.GetType().Name);
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "登录凭证无效");
        }
        catch (ArgumentException exception)
        {
            _logger.LogInformation("WS auth JWT 格式非法：{ExceptionType}", exception.GetType().Name);
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "登录凭证无效");
        }

        var userId = principal.FindFirst("sub")?.Value;
        var jwtId = principal.FindFirst("jti")?.Value;
        if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(jwtId))
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "登录凭证无效");
        }

        var session = await _db.Sessions.AsNoTracking()
            .FirstOrDefaultAsync(item => item.JwtId == jwtId, cancellationToken);
        if (session is null || !string.Equals(session.UserId, userId, StringComparison.Ordinal))
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "会话不存在，请重新登录");
        }

        if (session.RevokedAt is not null)
        {
            return WsAuthResult.Fail(ErrorCodes.AuthExpired, "会话已失效，请重新登录");
        }

        var status = await _db.Users.AsNoTracking()
            .Where(item => item.Id == userId)
            .Select(item => (UserStatus?)item.Status)
            .FirstOrDefaultAsync(cancellationToken);
        if (status != UserStatus.Active)
        {
            return WsAuthResult.Fail(ErrorCodes.AuthFailed, "账号不可用");
        }

        // WS 认证成功也刷新 last_seen_at（后续消息/心跳会继续更新）。
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await _db.Sessions
            .Where(item => item.Id == session.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastSeenAt, now), cancellationToken);

        return WsAuthResult.Success(userId);
    }
}
