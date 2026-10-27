using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-13-04：登录设备列表与远程下号单测。覆盖未撤销过滤、lastSeenAt 降序、current 标记、
/// 他人会话不泄漏（404）、未知会话 404 以及重复撤销的幂等性。
/// </summary>
public sealed class SessionTests
{
    [Fact]
    public async Task ListSessions_ReturnsOnlyActiveOrderedByLastSeen_AndMarksCurrent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "session-list@example.com");
        var first = await SeedSessionAsync(db, user.Id, "device-first", lastSeenAt: 100);
        var second = await SeedSessionAsync(db, user.Id, "device-second", lastSeenAt: 300);
        var third = await SeedSessionAsync(db, user.Id, "device-third", lastSeenAt: 200);
        await SeedSessionAsync(db, user.Id, "device-revoked", lastSeenAt: 400, revokedAt: 500);

        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());
        var sessions = await auth.ListSessionsAsync(user.Id, second.JwtId, CancellationToken.None);

        Assert.Equal(3, sessions.Count);
        Assert.Equal(new[] { "device-second", "device-third", "device-first" }, sessions.Select(item => item.DeviceId));
        Assert.True(sessions[0].Current);
        Assert.False(sessions[1].Current);
        Assert.False(sessions[2].Current);
        Assert.Equal(second.Id, sessions[0].SessionId);
        Assert.DoesNotContain(sessions, item => item.SessionId == first.Id && item.Current);
    }

    [Fact]
    public async Task ListSessions_ExcludesOtherUsersSessions()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "session-owner@example.com");
        var other = await TestSupport.SeedUserAsync(db, "session-other@example.com");
        await SeedSessionAsync(db, user.Id, "device-owner", lastSeenAt: 100);
        await SeedSessionAsync(db, other.Id, "device-other", lastSeenAt: 200);

        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());
        var sessions = await auth.ListSessionsAsync(user.Id, null, CancellationToken.None);

        Assert.Single(sessions);
        Assert.Equal("device-owner", sessions[0].DeviceId);
        Assert.False(sessions[0].Current);
    }

    [Fact]
    public async Task RevokeSession_SetsRevokedAt_AndIsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "session-revoke@example.com");
        var session = await SeedSessionAsync(db, user.Id, "device-revoke", lastSeenAt: 100);
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await auth.RevokeSessionAsync(user.Id, session.Id, CancellationToken.None);

        var revoked = await db.Sessions.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        Assert.NotNull(revoked.RevokedAt);
        var revokedAt = revoked.RevokedAt;

        var remaining = await auth.ListSessionsAsync(user.Id, null, CancellationToken.None);
        Assert.DoesNotContain(remaining, item => item.SessionId == session.Id);

        await auth.RevokeSessionAsync(user.Id, session.Id, CancellationToken.None);
        var again = await db.Sessions.AsNoTracking().SingleAsync(item => item.Id == session.Id);
        Assert.Equal(revokedAt, again.RevokedAt);
    }

    [Fact]
    public async Task RevokeSession_OtherUsersSession_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "session-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "session-b@example.com");
        var otherSession = await SeedSessionAsync(db, other.Id, "device-b", lastSeenAt: 100);
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => auth.RevokeSessionAsync(user.Id, otherSession.Id, CancellationToken.None),
            404);

        var stored = await db.Sessions.AsNoTracking().SingleAsync(item => item.Id == otherSession.Id);
        Assert.Null(stored.RevokedAt);
    }

    [Fact]
    public async Task RevokeSession_UnknownSession_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "session-unknown@example.com");
        var auth = TestSupport.CreateAuthService(db, TestSupport.CreateJwtOptions());

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => auth.RevokeSessionAsync(user.Id, Guid.NewGuid().ToString("D"), CancellationToken.None),
            404);
    }

    private static async Task<Session> SeedSessionAsync(
        ChatDbContext db,
        string userId,
        string deviceId,
        long lastSeenAt,
        long? revokedAt = null,
        string? jwtId = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var session = new Session
        {
            Id = Guid.NewGuid().ToString("D"),
            UserId = userId,
            DeviceId = deviceId,
            JwtId = jwtId ?? Guid.NewGuid().ToString("D"),
            CreatedAt = now,
            LastSeenAt = lastSeenAt,
            RevokedAt = revokedAt,
        };
        db.Sessions.Add(session);
        await db.SaveChangesAsync();
        return session;
    }
}
