using System.Globalization;
using System.Security.Cryptography;
using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Common.Security;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Email;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
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

        return await SendCoreAsync(email, purpose, ip, now, cancellationToken);
    }

    /// <summary>
    /// F-13：发送「找回密码」验证码。未注册/已禁用邮箱同样消耗限流额度并返回同样冷却时间，
    /// 但不会生成验证码记录、不会发信（避免通过响应差异枚举账号）。
    /// </summary>
    public async Task<EmailCodeSendResult> SendPasswordResetAsync(
        string? rawEmail,
        string? ip,
        CancellationToken cancellationToken)
    {
        if (!EmailAddress.TryNormalize(rawEmail, out var email))
        {
            throw ApiException.ValidationFailed("邮箱格式不正确");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var rejection = _rateLimiter.TryAcquire(email, ip, now);
        if (rejection != EmailCodeRateLimitRejection.None)
        {
            throw BuildRateLimitException(rejection);
        }

        var cooldownSec = Math.Max(1, _rateLimitOptions.EmailCodePerEmailIntervalSeconds);
        var userExists = await _db.Users
            .AsNoTracking()
            .AnyAsync(user => user.Email == email && user.Status == UserStatus.Active, cancellationToken);
        if (!userExists)
        {
            _logger.LogInformation("密码重置验证码请求：邮箱未注册，按冷却时间返回，不发送：{Email}", email);
            return new EmailCodeSendResult(email, EmailCodePurpose.Reset, cooldownSec, DevCode: null);
        }

        return await SendCoreAsync(email, EmailCodePurpose.Reset, ip, now, cancellationToken);
    }

    /// <summary>生成验证码、落库（只存哈希）并通过邮件通道发送；失败时清理刚写入的记录。</summary>
    private async Task<EmailCodeSendResult> SendCoreAsync(
        string email,
        EmailCodePurpose purpose,
        string? ip,
        long nowUtcMilliseconds,
        CancellationToken cancellationToken)
    {
        var code = GenerateCode();
        var entity = new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = email,
            Purpose = purpose,
            CodeHash = EmailCodeHasher.Hash(_jwtOptions.Key, email, purpose, code),
            ExpiresAt = nowUtcMilliseconds + ExpiresMinutes * 60_000L,
            Attempts = 0,
            CreatedAt = nowUtcMilliseconds,
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

    private static bool IsSixDigitCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length == CodeLength &&
        value.All(char.IsAsciiDigit);
}
