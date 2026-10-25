using Chat.Protocol;
using Chat.Server.Modules.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：已读服务单测（M3-05 行为）。覆盖单聊推进/不推进（Changed 判据）、越界与成员校验、
/// 群聊不返回对方 userId。契约见 protocol/protocol.md §4.6。
/// </summary>
public sealed class ReadServiceTests
{
    [Fact]
    public async Task UpdateReadAsync_DirectConversation_PersistsAndReportsPeerAndChanged()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "read-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "read-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, 3);
        db.ChangeTracker.Clear();
        var service = new ReadService(db, NullLogger<ReadService>.Instance);

        var result = await service.UpdateReadAsync(sender.Id, conversation.Id, 2, CancellationToken.None);

        Assert.Equal(2, result.LastReadSeq);
        Assert.True(result.Changed);
        Assert.Equal(other.Id, result.OtherUserId);

        var membership = await db.ConversationMembers.AsNoTracking()
            .SingleAsync(member => member.ConversationId == conversation.Id && member.UserId == sender.Id);
        Assert.Equal(2, membership.LastReadSeq);
    }

    [Fact]
    public async Task UpdateReadAsync_SameOrLowerValue_DoesNotChangeAndReportsNotChanged()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "read-same@example.com");
        var other = await TestSupport.SeedUserAsync(db, "read-same2@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, 3);
        db.ChangeTracker.Clear();
        var service = new ReadService(db, NullLogger<ReadService>.Instance);

        await service.UpdateReadAsync(sender.Id, conversation.Id, 2, CancellationToken.None);
        db.ChangeTracker.Clear();

        // 相同值：不推进（Changed=false 是「不广播 read.updated」的判据）。
        var same = await service.UpdateReadAsync(sender.Id, conversation.Id, 2, CancellationToken.None);
        Assert.False(same.Changed);
        Assert.Equal(2, same.LastReadSeq);

        // 更小值：只增不减，仍不推进。
        var lower = await service.UpdateReadAsync(sender.Id, conversation.Id, 1, CancellationToken.None);
        Assert.False(lower.Changed);
        Assert.Equal(2, lower.LastReadSeq);

        var membership = await db.ConversationMembers.AsNoTracking()
            .SingleAsync(member => member.ConversationId == conversation.Id && member.UserId == sender.Id);
        Assert.Equal(2, membership.LastReadSeq);
    }

    [Fact]
    public async Task UpdateReadAsync_ValidatesRangeMembershipAndGroupPeer()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "read-g-owner@example.com");
        var memberB = await TestSupport.SeedUserAsync(db, "read-g-b@example.com");
        var outsider = await TestSupport.SeedUserAsync(db, "read-g-out@example.com");
        var direct = await TestSupport.SeedDirectConversationAsync(db, owner, memberB);
        await TestSupport.SeedMessagesAsync(db, direct.Id, owner.Id, 2);
        var group = await TestSupport.SeedGroupConversationAsync(db, owner, memberB);
        await TestSupport.SeedMessagesAsync(db, group.Id, owner.Id, 2);
        db.ChangeTracker.Clear();
        var service = new ReadService(db, NullLogger<ReadService>.Instance);

        // 超出会话 lastSeq → VALIDATION_FAILED。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateReadAsync(owner.Id, direct.Id, 3, CancellationToken.None),
            400);

        // 非成员 → NOT_MEMBER。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.UpdateReadAsync(outsider.Id, direct.Id, 1, CancellationToken.None),
            403);

        // 群聊：推进成功但 OtherUserId=null（v1 不广播群已读）。
        var groupResult = await service.UpdateReadAsync(owner.Id, group.Id, 2, CancellationToken.None);
        Assert.True(groupResult.Changed);
        Assert.Null(groupResult.OtherUserId);
    }
}
