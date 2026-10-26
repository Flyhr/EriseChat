using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Modules.Friends;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-17 好友关系单测：申请（发送/幂等/自动接受/上限）、接受/拒绝/撤销、备注分组、删除好友。
/// 使用真实 SQLite 迁移库与真实 FriendService，不 mock 业务逻辑。
/// </summary>
public sealed class FriendServiceTests
{
    [Fact]
    public async Task SendRequest_CreatesPendingRequest()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-send-a@example.com", "用户 A");
        var userB = await TestSupport.SeedUserAsync(db, "friend-send-b@example.com", "用户 B");

        var service = TestSupport.CreateFriendService(db);
        var result = await service.SendRequestAsync(userA.Id, userB.Id, "你好", CancellationToken.None);

        Assert.False(result.AutoAccepted);
        Assert.Null(result.Friend);
        Assert.Equal(FriendRequestStatus.Pending, result.Request.Status);
        Assert.Equal(userA.Id, result.Request.FromUserId);
        Assert.Equal(userB.Id, result.Request.ToUserId);
        Assert.Equal("你好", result.Request.Message);

        var receiver = await service.ListRequestsAsync(userB.Id, CancellationToken.None);
        Assert.Single(receiver.Incoming);
        Assert.Empty(receiver.Outgoing);
        Assert.Equal(result.Request.RequestId, receiver.Incoming[0].RequestId);

        var sender = await service.ListRequestsAsync(userA.Id, CancellationToken.None);
        Assert.Empty(sender.Incoming);
        Assert.Single(sender.Outgoing);
    }

    [Fact]
    public async Task SendRequest_SameDirectionTwice_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-idem-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-idem-b@example.com");
        var service = TestSupport.CreateFriendService(db);

        var first = await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None);
        var second = await service.SendRequestAsync(userA.Id, userB.Id, "再试一次", CancellationToken.None);

        Assert.Equal(first.Request.RequestId, second.Request.RequestId);
        Assert.Equal(1, await db.FriendRequests.CountAsync(item => item.Status == FriendRequestStatus.Pending));
    }

    [Fact]
    public async Task SendRequest_ToSelf_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-self@example.com");
        var service = TestSupport.CreateFriendService(db);

        await TestSupport.AssertApiExceptionAsync(
            "VALIDATION_FAILED",
            () => service.SendRequestAsync(userA.Id, userA.Id, null, CancellationToken.None),
            expectedStatus: 400);
    }

    [Fact]
    public async Task SendRequest_UnknownTarget_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-missing-a@example.com");
        var service = TestSupport.CreateFriendService(db);

        await TestSupport.AssertApiExceptionAsync(
            "NOT_FOUND",
            () => service.SendRequestAsync(userA.Id, Guid.NewGuid().ToString("D"), null, CancellationToken.None),
            expectedStatus: 404);
    }

    [Fact]
    public async Task SendRequest_AlreadyFriend_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-already-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-already-b@example.com");
        await SeedFriendshipAsync(db, userA.Id, userB.Id);
        var service = TestSupport.CreateFriendService(db);

        await TestSupport.AssertApiExceptionAsync(
            "VALIDATION_FAILED",
            () => service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None),
            expectedStatus: 400);
    }

    [Fact]
    public async Task SendRequest_ReversePending_AutoAccepts()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-rev-a@example.com", "用户 A");
        var userB = await TestSupport.SeedUserAsync(db, "friend-rev-b@example.com", "用户 B");
        var service = TestSupport.CreateFriendService(db);

        // B 先申请 A；A 随后申请 B → 自动接受。
        var first = await service.SendRequestAsync(userB.Id, userA.Id, null, CancellationToken.None);
        var second = await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None);

        Assert.True(second.AutoAccepted);
        Assert.NotNull(second.Friend);
        Assert.Equal(userB.Id, second.Friend!.UserId);
        Assert.Equal(first.Request.RequestId, second.Request.RequestId);
        Assert.Equal(2, await db.Friends.CountAsync());
        Assert.Equal(0, await db.FriendRequests.CountAsync(item => item.Status == FriendRequestStatus.Pending));
        var pending = await db.FriendRequests.AsNoTracking().SingleAsync(item => item.Id == first.Request.RequestId);
        Assert.Equal(FriendRequestStatus.Accepted, pending.Status);
    }

    [Fact]
    public async Task Accept_CreatesBidirectionalFriendship()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-accept-a@example.com", "用户 A");
        var userB = await TestSupport.SeedUserAsync(db, "friend-accept-b@example.com", "用户 B");
        var service = TestSupport.CreateFriendService(db);

        var request = (await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None)).Request;
        var accepted = await service.AcceptAsync(userB.Id, request.RequestId, CancellationToken.None);

        Assert.Equal(FriendRequestStatus.Accepted, accepted.Request.Status);
        Assert.NotNull(accepted.Request.HandledAt);
        Assert.Equal(userA.Id, accepted.Friend.UserId);

        var rows = await db.Friends.AsNoTracking().ToListAsync();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, row => row.OwnerUserId == userA.Id && row.FriendUserId == userB.Id);
        Assert.Contains(rows, row => row.OwnerUserId == userB.Id && row.FriendUserId == userA.Id);

        var list = await service.ListAsync(userA.Id, CancellationToken.None);
        Assert.Single(list);
        Assert.Equal(userB.Id, list[0].UserId);
    }

    [Fact]
    public async Task Accept_AlreadyHandled_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-accept2-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-accept2-b@example.com");
        var service = TestSupport.CreateFriendService(db);

        var request = (await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None)).Request;
        await service.RejectAsync(userB.Id, request.RequestId, CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            "NOT_FOUND",
            () => service.AcceptAsync(userB.Id, request.RequestId, CancellationToken.None),
            expectedStatus: 404);
    }

    [Fact]
    public async Task Reject_SetsStatusAndHandledAt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-reject-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-reject-b@example.com");
        var service = TestSupport.CreateFriendService(db);

        var request = (await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None)).Request;
        var rejected = await service.RejectAsync(userB.Id, request.RequestId, CancellationToken.None);

        Assert.Equal(FriendRequestStatus.Rejected, rejected.Status);
        Assert.NotNull(rejected.HandledAt);
        var receiver = await service.ListRequestsAsync(userB.Id, CancellationToken.None);
        Assert.Empty(receiver.Incoming);
        Assert.Equal(0, await db.Friends.CountAsync());
    }

    [Fact]
    public async Task Reject_ByNonRecipient_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-reject3-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-reject3-b@example.com");
        var outsider = await TestSupport.SeedUserAsync(db, "friend-reject3-c@example.com");
        var service = TestSupport.CreateFriendService(db);

        var request = (await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None)).Request;

        await TestSupport.AssertApiExceptionAsync(
            "NOT_FOUND",
            () => service.RejectAsync(outsider.Id, request.RequestId, CancellationToken.None),
            expectedStatus: 404);
    }

    [Fact]
    public async Task Cancel_BySender_SetsCancelled()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-cancel-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-cancel-b@example.com");
        var service = TestSupport.CreateFriendService(db);

        var request = (await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None)).Request;
        var cancelled = await service.CancelAsync(userA.Id, request.RequestId, CancellationToken.None);

        Assert.Equal(FriendRequestStatus.Cancelled, cancelled.Status);
        var sender = await service.ListRequestsAsync(userA.Id, CancellationToken.None);
        Assert.Empty(sender.Outgoing);
    }

    [Fact]
    public async Task UpdateFriend_RemarkAndGroup_Saved()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-update-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-update-b@example.com", "用户 B");
        await SeedFriendshipAsync(db, userA.Id, userB.Id);
        var service = TestSupport.CreateFriendService(db);

        var updated = await service.UpdateFriendAsync(userA.Id, userB.Id, "  老王  ", "  项目组  ", CancellationToken.None);

        Assert.Equal("老王", updated.Remark);
        Assert.Equal("项目组", updated.GroupName);
        var list = await service.ListAsync(userA.Id, CancellationToken.None);
        Assert.Equal("老王", list[0].Remark);
        Assert.Equal("项目组", list[0].GroupName);
    }

    [Fact]
    public async Task UpdateFriend_EmptyString_ClearsFields()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-clear-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-clear-b@example.com");
        await SeedFriendshipAsync(db, userA.Id, userB.Id);
        var service = TestSupport.CreateFriendService(db);

        await service.UpdateFriendAsync(userA.Id, userB.Id, "老王", "项目组", CancellationToken.None);
        var cleared = await service.UpdateFriendAsync(userA.Id, userB.Id, string.Empty, string.Empty, CancellationToken.None);

        Assert.Null(cleared.Remark);
        Assert.Null(cleared.GroupName);
    }

    [Fact]
    public async Task UpdateFriend_NotFriend_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-notfriend-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-notfriend-b@example.com");
        var service = TestSupport.CreateFriendService(db);

        await TestSupport.AssertApiExceptionAsync(
            "NOT_FOUND",
            () => service.UpdateFriendAsync(userA.Id, userB.Id, "备注", null, CancellationToken.None),
            expectedStatus: 404);
    }

    [Fact]
    public async Task RemoveFriend_DeletesBidirectionalRows()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-remove-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-remove-b@example.com");
        await SeedFriendshipAsync(db, userA.Id, userB.Id);
        var service = TestSupport.CreateFriendService(db);

        var removed = await service.RemoveAsync(userA.Id, userB.Id, CancellationToken.None);

        Assert.Equal(userB.Id, removed.Own.UserId);
        Assert.Equal(userA.Id, removed.Other.UserId);
        Assert.Equal(0, await db.Friends.CountAsync());
        Assert.Empty(await service.ListAsync(userA.Id, CancellationToken.None));
    }

    [Fact]
    public async Task SendRequest_TooManyPending_RateLimited()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "friend-limit-sender@example.com");
        var service = TestSupport.CreateFriendService(db);

        for (var index = 0; index < FriendService.PendingOutgoingLimit; index++)
        {
            var target = await TestSupport.SeedUserAsync(db, $"friend-limit-{index}@example.com");
            await service.SendRequestAsync(sender.Id, target.Id, null, CancellationToken.None);
        }

        var extra = await TestSupport.SeedUserAsync(db, "friend-limit-extra@example.com");
        await TestSupport.AssertApiExceptionAsync(
            "RATE_LIMITED",
            () => service.SendRequestAsync(sender.Id, extra.Id, null, CancellationToken.None),
            expectedStatus: 429);
    }

    [Fact]
    public async Task ListRequests_OnlyPendingReturned()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var userA = await TestSupport.SeedUserAsync(db, "friend-pending-a@example.com");
        var userB = await TestSupport.SeedUserAsync(db, "friend-pending-b@example.com");
        var userC = await TestSupport.SeedUserAsync(db, "friend-pending-c@example.com");
        var service = TestSupport.CreateFriendService(db);

        await service.SendRequestAsync(userA.Id, userB.Id, null, CancellationToken.None);
        var cRequest = (await service.SendRequestAsync(userC.Id, userB.Id, null, CancellationToken.None)).Request;
        await service.RejectAsync(userB.Id, cRequest.RequestId, CancellationToken.None);

        var result = await service.ListRequestsAsync(userB.Id, CancellationToken.None);

        Assert.Single(result.Incoming);
        Assert.Equal(userA.Id, result.Incoming[0].FromUserId);
        Assert.Empty(result.Outgoing);
    }

    /// <summary>直接落库双向好友关系（跳过申请流程）。</summary>
    private static async Task SeedFriendshipAsync(ChatDbContext db, string ownerUserId, string friendUserId)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        db.Friends.Add(new Chat.Server.Data.Entities.Friend
        {
            OwnerUserId = ownerUserId,
            FriendUserId = friendUserId,
            CreatedAt = now,
        });
        db.Friends.Add(new Chat.Server.Data.Entities.Friend
        {
            OwnerUserId = friendUserId,
            FriendUserId = ownerUserId,
            CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }
}
