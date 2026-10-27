using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-13-03：邮箱验证码找回密码单测。覆盖正确重置、旧密码失效、用途不匹配、验证码过期、
/// 未知邮箱不泄漏且不发信、成功消费后不可重放、新密码规则校验与发送限流。
/// </summary>
public sealed class PasswordResetTests
{
    private static (AuthService Auth, EmailCodeService EmailCodes) CreateServices(
        Chat.Server.Data.ChatDbContext db,
        Chat.Server.Common.Options.JwtOptions jwtOptions,
        Chat.Server.Common.Options.RateLimitOptions rateLimitOptions)
    {
        var auth = TestSupport.CreateAuthService(db, jwtOptions, rateLimitOptions);
        var emailCodes = TestSupport.CreateEmailCodeService(
            db, jwtOptions, TestSupport.CreateEmailOptions("123456"), rateLimitOptions);
        return (auth, emailCodes);
    }

    [Fact]
    public async Task ResetPassword_WithValidCode_ReplacesHashAndOldPasswordFails()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var rate = TestSupport.CreateRateLimitOptions();
        var (auth, emailCodes) = CreateServices(db, jwtOptions, rate);
        var user = await TestSupport.SeedUserAsync(
            db, "reset-ok@example.com", passwordHash: PasswordHasher.Hash("old-password-1"));

        var sent = await emailCodes.SendPasswordResetAsync("reset-ok@example.com", "127.0.0.1", CancellationToken.None);
        Assert.Equal("123456", sent.DevCode);

        await auth.ResetPasswordAsync("reset-ok@example.com", "123456", "new-password-9", CancellationToken.None);

        var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.True(PasswordHasher.Verify("new-password-9", stored.PasswordHash!));
        Assert.False(PasswordHasher.Verify("old-password-1", stored.PasswordHash!));
        Assert.Equal(0, await db.EmailCodes.CountAsync());
    }

    [Fact]
    public async Task ResetPassword_CodeIssuedForOtherPurpose_CodeInvalid()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (auth, _) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        await TestSupport.SeedUserAsync(db, "reset-purpose@example.com");
        await db.EmailCodes.AddAsync(new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = "reset-purpose@example.com",
            Purpose = EmailCodePurpose.Register,
            CodeHash = EmailCodeHasher.Hash(jwtOptions.Key, "reset-purpose@example.com", EmailCodePurpose.Register, "123456"),
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds(),
            Attempts = 0,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        await db.SaveChangesAsync();

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.CodeInvalid,
            () => auth.ResetPasswordAsync("reset-purpose@example.com", "123456", "new-password-9", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task ResetPassword_ExpiredCode_CodeExpired()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (auth, _) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        await TestSupport.SeedUserAsync(db, "reset-expired@example.com");
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.EmailCodes.AddAsync(new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = "reset-expired@example.com",
            Purpose = EmailCodePurpose.Reset,
            CodeHash = EmailCodeHasher.Hash(jwtOptions.Key, "reset-expired@example.com", EmailCodePurpose.Reset, "123456"),
            ExpiresAt = now - 1_000,
            Attempts = 0,
            CreatedAt = now - 600_000,
        });
        await db.SaveChangesAsync();

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.CodeExpired,
            () => auth.ResetPasswordAsync("reset-expired@example.com", "123456", "new-password-9", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task RequestPasswordReset_UnknownEmail_ReturnsCooldownWithoutSending()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (_, emailCodes) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());

        var result = await emailCodes.SendPasswordResetAsync("nobody@example.com", "127.0.0.1", CancellationToken.None);

        Assert.Equal(60, result.CooldownSec);
        Assert.Null(result.DevCode);
        Assert.Equal(0, await db.EmailCodes.CountAsync());
    }

    [Fact]
    public async Task ResetPassword_UnknownEmail_ConsumesCodeAndDoesNotReveal()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (auth, _) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await db.EmailCodes.AddAsync(new EmailCode
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = "ghost@example.com",
            Purpose = EmailCodePurpose.Reset,
            CodeHash = EmailCodeHasher.Hash(jwtOptions.Key, "ghost@example.com", EmailCodePurpose.Reset, "123456"),
            ExpiresAt = now + 600_000,
            Attempts = 0,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Unauthorized,
            () => auth.ResetPasswordAsync("ghost@example.com", "123456", "new-password-9", CancellationToken.None),
            401);
        Assert.Equal(0, await db.EmailCodes.CountAsync());
    }

    [Fact]
    public async Task ResetPassword_CodeCannotBeReused()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (auth, emailCodes) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        await TestSupport.SeedUserAsync(db, "reset-replay@example.com", passwordHash: PasswordHasher.Hash("old-password-1"));
        await emailCodes.SendPasswordResetAsync("reset-replay@example.com", "127.0.0.1", CancellationToken.None);

        await auth.ResetPasswordAsync("reset-replay@example.com", "123456", "new-password-9", CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.CodeInvalid,
            () => auth.ResetPasswordAsync("reset-replay@example.com", "123456", "another-password-1", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task ResetPassword_TooShortNewPassword_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (auth, emailCodes) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        await TestSupport.SeedUserAsync(db, "reset-short@example.com");
        await emailCodes.SendPasswordResetAsync("reset-short@example.com", "127.0.0.1", CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.ResetPasswordAsync("reset-short@example.com", "123456", "short", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task RequestPasswordReset_SecondTimeWithinInterval_RateLimited()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var jwtOptions = TestSupport.CreateJwtOptions();
        var (_, emailCodes) = CreateServices(db, jwtOptions, TestSupport.CreateRateLimitOptions());
        await TestSupport.SeedUserAsync(db, "reset-rate@example.com");
        await emailCodes.SendPasswordResetAsync("reset-rate@example.com", "127.0.0.1", CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.EmailRateLimited,
            () => emailCodes.SendPasswordResetAsync("reset-rate@example.com", "127.0.0.1", CancellationToken.None),
            429);
    }
}
