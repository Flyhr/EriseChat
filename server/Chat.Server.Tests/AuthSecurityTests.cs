using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// M1-09：认证安全关键路径单测。覆盖验证码只存哈希/10 分钟有效、过期与错误次数、发送限流阈值、
/// 登录失败锁定阈值（真实 SQLite 临时库 + 真实迁移）。
/// </summary>
public sealed class AuthSecurityTests
{
    [Fact]
    public void EmailCodeHasher_StoresOnlyHashAndVerifiesWithFixedTimeCompare()
    {
        var key = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        var hash = EmailCodeHasher.Hash(key, "a@example.com", EmailCodePurpose.Register, "123456");

        Assert.NotEqual("123456", hash);
        Assert.Equal(44, hash.Length);
        Assert.True(EmailCodeHasher.Verify(key, "a@example.com", EmailCodePurpose.Register, "123456", hash));
        Assert.False(EmailCodeHasher.Verify(key, "a@example.com", EmailCodePurpose.Register, "654321", hash));
        Assert.False(EmailCodeHasher.Verify(key, "a@example.com", EmailCodePurpose.Login, "123456", hash));
        Assert.False(EmailCodeHasher.Verify(key, "b@example.com", EmailCodePurpose.Register, "123456", hash));
    }

    [Fact]
    public async Task SendAsync_PersistsOnlyHashWithTenMinuteExpiry()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var service = TestSupport.CreateEmailCodeService(
            db, jwtOptions, TestSupport.CreateEmailOptions("123456"), TestSupport.CreateRateLimitOptions());

        var result = await service.SendAsync("user@example.com", EmailCodePurpose.Register, "127.0.0.1", CancellationToken.None);

        Assert.Equal("user@example.com", result.Email);
        Assert.Equal(60, result.CooldownSec);
        Assert.Equal("123456", result.DevCode);

        var stored = await db.EmailCodes.AsNoTracking().SingleAsync();
        Assert.NotEqual("123456", stored.CodeHash);
        Assert.Equal(44, stored.CodeHash.Length);
        Assert.True(EmailCodeHasher.Verify(jwtOptions.Key, "user@example.com", EmailCodePurpose.Register, "123456", stored.CodeHash));
        Assert.Equal(EmailCodeService.ExpiresMinutes * 60_000L, stored.ExpiresAt - stored.CreatedAt);
        Assert.Equal(0, stored.Attempts);
        Assert.Equal("127.0.0.1", stored.Ip);
    }

    [Fact]
    public async Task LoginWithCodeAsync_RejectsExpiredCode()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var auth = TestSupport.CreateAuthService(db, jwtOptions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.EmailCodes.Add(new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = "expired@example.com",
            Purpose = EmailCodePurpose.Login,
            CodeHash = EmailCodeHasher.Hash(jwtOptions.Key, "expired@example.com", EmailCodePurpose.Login, "123456"),
            ExpiresAt = now - 1,
            Attempts = 0,
            CreatedAt = now - EmailCodeService.ExpiresMinutes * 60_000L - 1000,
        });
        await db.SaveChangesAsync();

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.CodeExpired,
            () => auth.LoginWithCodeAsync("expired@example.com", "123456", "test-device", CancellationToken.None));
    }

    [Fact]
    public async Task LoginWithCodeAsync_IncrementsAttemptsUntilExceeded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var auth = TestSupport.CreateAuthService(db, jwtOptions);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.EmailCodes.Add(new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = "attempts@example.com",
            Purpose = EmailCodePurpose.Login,
            CodeHash = EmailCodeHasher.Hash(jwtOptions.Key, "attempts@example.com", EmailCodePurpose.Login, "123456"),
            ExpiresAt = now + EmailCodeService.ExpiresMinutes * 60_000L,
            Attempts = 0,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        // 错误 4 次：返回 CODE_INVALID；第 5 次达到上限：CODE_ATTEMPTS_EXCEEDED。
        for (var attempt = 1; attempt <= 4; attempt++)
        {
            await TestSupport.AssertApiExceptionAsync(
                ErrorCodes.CodeInvalid,
                () => auth.LoginWithCodeAsync("attempts@example.com", "000000", "test-device", CancellationToken.None));
        }

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.CodeAttemptsExceeded,
            () => auth.LoginWithCodeAsync("attempts@example.com", "000000", "test-device", CancellationToken.None));

        var stored = await db.EmailCodes.AsNoTracking().SingleAsync();
        Assert.Equal(5, stored.Attempts);
    }

    [Fact]
    public void EmailCodeRateLimiter_EnforcesIntervalDailyAndIpHourlyThresholds()
    {
        var options = TestSupport.CreateRateLimitOptions();
        options.EmailCodePerEmailIntervalSeconds = 60;
        options.EmailCodePerEmailDaily = 3;
        options.EmailCodePerIpHourly = 2;
        var limiter = new EmailCodeRateLimiter(Microsoft.Extensions.Options.Options.Create(options));
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // 同邮箱 60 秒间隔。
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire("interval@example.com", null, start));
        Assert.Equal(EmailCodeRateLimitRejection.EmailTooFrequent, limiter.TryAcquire("interval@example.com", null, start + 30_000));
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire("interval@example.com", null, start + 61_000));

        // 同邮箱每天 3 次。
        var dailyEmail = "daily@example.com";
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire(dailyEmail, null, start));
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire(dailyEmail, null, start + 61_000));
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire(dailyEmail, null, start + 122_000));
        Assert.Equal(EmailCodeRateLimitRejection.EmailDailyExceeded, limiter.TryAcquire(dailyEmail, null, start + 183_000));

        // 同 IP 每小时 2 次（不同邮箱共享 IP 计数）。
        const string ip = "10.0.0.1";
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire("ip-a@example.com", ip, start));
        Assert.Equal(EmailCodeRateLimitRejection.None, limiter.TryAcquire("ip-b@example.com", ip, start));
        Assert.Equal(EmailCodeRateLimitRejection.IpHourlyExceeded, limiter.TryAcquire("ip-c@example.com", ip, start));
    }

    [Fact]
    public void LoginAttemptLimiter_LocksAfterThresholdAndClears()
    {
        var options = TestSupport.CreateRateLimitOptions();
        options.LoginFailedMaxAttempts = 5;
        options.LoginLockMinutes = 5;
        var limiter = new LoginAttemptLimiter(Microsoft.Extensions.Options.Options.Create(options));
        var start = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        const string email = "lock@example.com";

        for (var attempt = 1; attempt <= 4; attempt++)
        {
            Assert.Equal(0, limiter.RecordFailure(email, start + attempt));
            Assert.Equal(0, limiter.GetRemainingLockMilliseconds(email, start + attempt));
        }

        var lockedMilliseconds = limiter.RecordFailure(email, start + 5);
        Assert.True(lockedMilliseconds > 0);
        Assert.True(limiter.GetRemainingLockMilliseconds(email, start + 5) > 0);

        // 锁定窗口过后自动解锁；Clear 可提前清除（登录成功）。
        Assert.Equal(0, limiter.GetRemainingLockMilliseconds(email, start + 5 + 5 * 60_000L + 1));
        limiter.Clear(email);
        Assert.Equal(0, limiter.GetRemainingLockMilliseconds(email, start + 6));
    }

    [Fact]
    public void JwtTokenService_IssuesTokenWithThreeDayLifetime()
    {
        var jwtOptions = TestSupport.CreateJwtOptions();
        var service = new JwtTokenService(Microsoft.Extensions.Options.Options.Create(jwtOptions));
        var user = new User
        {
            Id = "jwt-expiry-user",
            Email = "jwt-expiry@example.com",
            DisplayName = "JWT Expiry",
            Role = UserRole.User,
            Status = UserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        var now = DateTimeOffset.UtcNow;
        var issued = service.CreateToken(user, "jwt-expiry-jti", now);
        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().ReadJwtToken(issued.AccessToken);

        // exp - iat（无 iat 时以 nbf 为准）≈ 3 天，容差 ±5 分钟。
        var lifetime = token.ValidTo - now.UtcDateTime;
        Assert.InRange(lifetime.TotalSeconds, 3 * 24 * 3600 - 300, 3 * 24 * 3600 + 300);

        // JWT 的 exp 为秒精度，返回给前端的 ExpiresAt 可与之相差不到 1 秒。
        var expMilliseconds = new DateTimeOffset(token.ValidTo).ToUnixTimeMilliseconds();
        Assert.InRange(Math.Abs(issued.ExpiresAt - expMilliseconds), 0, 1000);
    }
}
