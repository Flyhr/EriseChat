using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Auth;

/// <summary>验证码发送结果（DevCode 仅 Development 返回，供前端/联调预填）。</summary>
public sealed record EmailCodeSendResult(string Email, EmailCodePurpose Purpose, int CooldownSec, string? DevCode);

/// <summary>
/// 邮箱验证码服务：生成 6 位数字 → HMAC-SHA256 哈希落库（只存哈希）→ Mock/SMTP 通道发送。
/// 规则（计划 §4.1 / AGENTS §6）：10 分钟有效、同邮箱 60 秒/次且 10 次/天、同 IP 20 次/小时、
/// 注册关闭或域名白名单不匹配时拒绝。限流计数在单进程内存中（重启清零）。
/// </summary>
public sealed class EmailCodeService
{
    /// <summary>验证码有效期（分钟）。</summary>
    public const int ExpiresMinutes = 10;

    private const int CodeLength = 6;

    private readonly ChatDbContext _db;
    private readonly AppOptions _appOptions;
    private readonly JwtOptions _jwtOptions;
    private readonly EmailOptions _emailOptions;
    private readonly RateLimitOptions _rateLimitOptions;
    private readonly EmailCodeRateLimiter _rateLimiter;
    private readonly IEmailSender _emailSender;
    private readonly IHostEnvironment _environment;
    private readonly ILogger<EmailCodeService> _logger;

    public EmailCodeService(
        ChatDbContext db,
        IOptions<AppOptions> appOptions,
        IOptions<JwtOptions> jwtOptions,
        IOptions<EmailOptions> emailOptions,
        IOptions<RateLimitOptions> rateLimitOptions,
        EmailCodeRateLimiter rateLimiter,
        IEmailSender emailSender,
        IHostEnvironment environment,
        ILogger<EmailCodeService> logger)
    {
        _db = db;
        _appOptions = appOptions.Value;
        _jwtOptions = jwtOptions.Value;
        _emailOptions = emailOptions.Value;
        _rateLimitOptions = rateLimitOptions.Value;
        _rateLimiter = rateLimiter;
        _emailSender = emailSender;
        _environment = environment;
        _logger = logger;
    }

    /// <summary>发送验证码。失败抛 <see cref="ApiException"/>（由统一错误中间件转 JSON）。</summary>
    public async Task<EmailCodeSendResult> SendAsync(
        string? rawEmail,
        EmailCodePurpose purpose,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (!TryNormalizeEmail(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        EnsureSendPolicy(email, purpose);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rejection = _rateLimiter.TryAcquire(email, ip, now);
        if (rejection != EmailCodeRateLimitRejection.None)
        {
            throw BuildRateLimitException(rejection);
        }

        var code = GenerateCode();
        var entity = new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = email,
            Purpose = purpose,
            CodeHash = HashCode(email, purpose, code),
            ExpiresAt = now + ExpiresMinutes * 60_000L,
            Attempts = 0,
            CreatedAt = now,
            Ip = string.IsNullOrWhiteSpace(ip) ? null : ip,
        };

        _db.EmailCodes.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        try
        {
            await _emailSender.SendCodeAsync(email, code, purpose, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // 发送失败：删除刚写入的验证码记录并向上抛错（记录原因，不记录验证码明文）。
            _logger.LogWarning(exception, "验证码邮件发送失败，清理验证码记录：{Email}，{Purpose}", email, purpose);
            _db.EmailCodes.Remove(entity);
            try
            {
                await _db.SaveChangesAsync(CancellationToken.None);
            }
            catch (Exception cleanupException)
            {
                _logger.LogError(cleanupException, "清理验证码记录失败：{Email}", email);
            }

            throw new InvalidOperationException("验证码邮件发送失败", exception);
        }

        _logger.LogInformation("验证码已发送：{Email}，{Purpose}，过期时间 {ExpiresAt}", email, purpose, entity.ExpiresAt);
        return new EmailCodeSendResult(
            email,
            purpose,
            CooldownSec: Math.Max(1, _rateLimitOptions.EmailCodePerEmailIntervalSeconds),
            DevCode: _environment.IsDevelopment() ? code : null);
    }

    /// <summary>注册策略：注册开关 + 邮箱域名白名单（白名单为空 = 不限制）。</summary>
    private void EnsureSendPolicy(string email, EmailCodePurpose purpose)
    {
        if (purpose != EmailCodePurpose.Register)
        {
            return;
        }

        if (!_appOptions.RegistrationEnabled)
        {
            throw new ApiException(
                ErrorCodes.RegistrationDisabled,
                "注册功能已关闭",
                StatusCodes.Status403Forbidden);
        }

        var allowedDomains = _appOptions.AllowedEmailDomains;
        if (allowedDomains is not { Length: > 0 })
        {
            return;
        }

        var atIndex = email.IndexOf('@');
        var domain = atIndex >= 0 ? email[(atIndex + 1)..] : string.Empty;
        var allowed = allowedDomains.Any(item =>
            !string.IsNullOrWhiteSpace(item) &&
            string.Equals(item.Trim(), domain, StringComparison.OrdinalIgnoreCase));
        if (!allowed)
        {
            throw new ApiException(
                ErrorCodes.EmailDomainNotAllowed,
                "该邮箱域名不在允许列表内",
                StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>Development 且配置了 6 位固定测试码时使用固定码（便于联调），否则随机 6 位数字。</summary>
    private string GenerateCode()
    {
        var fixedCode = _emailOptions.Mock.FixedCode;
        if (_environment.IsDevelopment() && IsSixDigitCode(fixedCode))
        {
            return fixedCode;
        }

        return RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }

    /// <summary>验证码哈希：HMAC-SHA256(服务端密钥, email + purpose + code)，只存 Base64 结果。</summary>
    private string HashCode(string email, EmailCodePurpose purpose, string code)
    {
        var key = _jwtOptions.Key;
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException("JWT 密钥为空，无法计算验证码哈希（首次启动应已生成 secrets.json）");
        }

        var payload = $"{email}\n{purpose.ToString().ToLowerInvariant()}\n{code}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));
    }

    private ApiException BuildRateLimitException(EmailCodeRateLimitRejection rejection) => rejection switch
    {
        EmailCodeRateLimitRejection.EmailTooFrequent => new ApiException(
            ErrorCodes.EmailRateLimited,
            $"发送过于频繁，请 {Math.Max(1, _rateLimitOptions.EmailCodePerEmailIntervalSeconds)} 秒后再试",
            StatusCodes.Status429TooManyRequests),
        EmailCodeRateLimitRejection.EmailDailyExceeded => new ApiException(
            ErrorCodes.EmailRateLimited,
            "该邮箱今日发送次数已达上限",
            StatusCodes.Status429TooManyRequests),
        EmailCodeRateLimitRejection.IpHourlyExceeded => new ApiException(
            ErrorCodes.EmailRateLimited,
            "当前网络发送次数已达上限，请稍后再试",
            StatusCodes.Status429TooManyRequests),
        _ => new ApiException(ErrorCodes.EmailRateLimited, "发送过于频繁，请稍后再试", StatusCodes.Status429TooManyRequests),
    };

    private static bool TryNormalizeEmail(string? rawEmail, out string normalized)
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

    private static bool IsSixDigitCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == CodeLength &&
        value.All(char.IsAsciiDigit);
}
