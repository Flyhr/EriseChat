using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-13-02：修改/设置密码单测。覆盖旧密码校验、从未设置密码直接设置、长度边界、
/// 新旧相同拒绝、错误旧密码返回 PASSWORD_INVALID，以及 /api/me 的 hasPassword 状态。
/// </summary>
public sealed class PasswordTests
{
    [Fact]
    public async Task ChangePassword_WithCorrectOldPassword_ReplacesHash()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var oldPassword = "old-password-1";
        var user = await TestSupport.SeedUserAsync(
            db, "password-ok@example.com", passwordHash: PasswordHasher.Hash(oldPassword));
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await auth.ChangePasswordAsync(user.Id, oldPassword, "new-password-2", CancellationToken.None);

        var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.NotNull(stored.PasswordHash);
        Assert.True(PasswordHasher.Verify("new-password-2", stored.PasswordHash!));
        Assert.False(PasswordHasher.Verify(oldPassword, stored.PasswordHash!));
    }

    [Fact]
    public async Task ChangePassword_WrongOldPassword_PasswordInvalid()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(
            db, "password-wrong-old@example.com", passwordHash: PasswordHasher.Hash("old-password-1"));
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.PasswordInvalid,
            () => auth.ChangePasswordAsync(user.Id, "wrong-old-password", "new-password-2", CancellationToken.None),
            401);

        var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.True(PasswordHasher.Verify("old-password-1", stored.PasswordHash!));
    }

    [Fact]
    public async Task ChangePassword_MissingOldPassword_PasswordInvalid()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(
            db, "password-missing-old@example.com", passwordHash: PasswordHasher.Hash("old-password-1"));
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.PasswordInvalid,
            () => auth.ChangePasswordAsync(user.Id, null, "new-password-2", CancellationToken.None),
            401);
    }

    [Fact]
    public async Task ChangePassword_NoExistingPassword_SetsDirectly()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "password-first-set@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await auth.ChangePasswordAsync(user.Id, null, "first-password-1", CancellationToken.None);

        var stored = await db.Users.AsNoTracking().SingleAsync(item => item.Id == user.Id);
        Assert.True(PasswordHasher.Verify("first-password-1", stored.PasswordHash!));
    }

    [Theory]
    [InlineData("a")]
    [InlineData("1234567")]
    public async Task ChangePassword_TooShort_ValidationFailed(string newPassword)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(
            db, "password-too-short@example.com", passwordHash: PasswordHasher.Hash("old-password-1"));
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.ChangePasswordAsync(user.Id, "old-password-1", newPassword, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task ChangePassword_SameAsCurrent_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(
            db, "password-same@example.com", passwordHash: PasswordHasher.Hash("same-password-1"));
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => auth.ChangePasswordAsync(user.Id, "same-password-1", "same-password-1", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Profile_HasPasswordReflectsPasswordState()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "password-profile@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        var before = await auth.GetProfileAsync(user.Id, CancellationToken.None);
        Assert.False(before.HasPassword);

        await auth.ChangePasswordAsync(user.Id, null, "profile-password-1", CancellationToken.None);

        var after = await auth.GetProfileAsync(user.Id, CancellationToken.None);
        Assert.True(after.HasPassword);
    }

    [Fact]
    public async Task ChangePassword_UnknownUser_Unauthorized()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Unauthorized,
            () => auth.ChangePasswordAsync("missing-user", null, "new-password-2", CancellationToken.None),
            401);
    }
}
