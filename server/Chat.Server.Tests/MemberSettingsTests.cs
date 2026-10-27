using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-10：会话成员设置（置顶/免打扰）单测。
/// 契约见 protocol/protocol.md §4.5（pinned/muted 按收件人填充、不广播、客户端排序）。
/// </summary>
public sealed class MemberSettingsTests
{
    [Fact]
    public async Task UpdateMemberSettingsAsync_SetPinned_ReturnsPinnedAndPersists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-owner@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-member@example.com", "乙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateMemberSettingsAsync(
            owner.Id,
            conversation.Id,
            pinned: true,
            muted: null,
            CancellationToken.None);

        Assert.True(updated.Pinned);
        Assert.False(updated.Muted);
        Assert.Equal(conversation.Id, updated.ConversationId);

        var row = await db.ConversationMembers.AsNoTracking()
            .SingleAsync(item => item.ConversationId == conversation.Id && item.UserId == owner.Id);
        Assert.True(row.Pinned);
        Assert.False(row.Muted);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_UnsetPinned_KeepsListOrderUnchanged()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o2@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-m2@example.com", "乙");
        var other = await TestSupport.SeedUserAsync(db, "ms-m2b@example.com", "丙");
        var first = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        await TestSupport.SeedMessagesAsync(db, first.Id, owner.Id, count: 1, startSeq: 1);
        var second = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        await TestSupport.SeedMessagesAsync(db, second.Id, owner.Id, count: 1, startSeq: 1);
        var service = TestSupport.CreateConversationService(db);

        var before = (await service.ListAsync(owner.Id, CancellationToken.None))
            .Select(item => item.ConversationId)
            .ToList();
        Assert.Equal(2, before.Count);

        await service.UpdateMemberSettingsAsync(owner.Id, first.Id, pinned: true, muted: null, CancellationToken.None);
        var pinnedList = await service.ListAsync(owner.Id, CancellationToken.None);
        // 服务端顺序不变（置顶排序只在前端）：置顶不改变 conv.list 的返回顺序。
        Assert.Equal(before, pinnedList.Select(item => item.ConversationId).ToList());
        Assert.True(pinnedList.Single(item => item.ConversationId == first.Id).Pinned);

        var updated = await service.UpdateMemberSettingsAsync(owner.Id, first.Id, pinned: false, muted: null, CancellationToken.None);
        Assert.False(updated.Pinned);
        Assert.Equal(before, (await service.ListAsync(owner.Id, CancellationToken.None))
            .Select(item => item.ConversationId)
            .ToList());
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_MutedIsIndependentOfPinned()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o3@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-m3@example.com", "乙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var mutedOnly = await service.UpdateMemberSettingsAsync(
            owner.Id, conversation.Id, pinned: null, muted: true, CancellationToken.None);
        Assert.False(mutedOnly.Pinned);
        Assert.True(mutedOnly.Muted);

        var both = await service.UpdateMemberSettingsAsync(
            owner.Id, conversation.Id, pinned: true, muted: null, CancellationToken.None);
        Assert.True(both.Pinned);
        Assert.True(both.Muted);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_EmptyBody_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o4@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-m4@example.com", "乙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateMemberSettingsAsync(owner.Id, conversation.Id, pinned: null, muted: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_NotMember_NotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o5@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-m5@example.com", "乙");
        var outsider = await TestSupport.SeedUserAsync(db, "ms-x5@example.com", "丙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.UpdateMemberSettingsAsync(outsider.Id, conversation.Id, pinned: true, muted: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_ConversationNotFound_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o6@example.com", "甲");
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.UpdateMemberSettingsAsync(owner.Id, Guid.NewGuid().ToString("D"), pinned: true, muted: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_TwoUsersSettingsAreIsolated()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o7@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ms-m7@example.com", "乙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await service.UpdateMemberSettingsAsync(owner.Id, conversation.Id, pinned: true, muted: true, CancellationToken.None);

        var ownerView = (await service.ListAsync(owner.Id, CancellationToken.None)).Single();
        var memberView = (await service.ListAsync(member.Id, CancellationToken.None)).Single();
        Assert.True(ownerView.Pinned);
        Assert.True(ownerView.Muted);
        Assert.False(memberView.Pinned);
        Assert.False(memberView.Muted);
    }

    [Fact]
    public async Task UpdateMemberSettingsAsync_GroupConversationAndListReturnsPerRecipientSettings()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ms-o8@example.com", "群主");
        var member = await TestSupport.SeedUserAsync(db, "ms-m8@example.com", "成员");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateMemberSettingsAsync(
            member.Id, conversation.Id, pinned: null, muted: true, CancellationToken.None);
        Assert.Equal(ConversationType.Group, updated.Type);
        Assert.True(updated.Muted);
        Assert.False(updated.Pinned);

        var ownerView = (await service.ListAsync(owner.Id, CancellationToken.None)).Single();
        var memberView = (await service.ListAsync(member.Id, CancellationToken.None)).Single();
        Assert.False(ownerView.Muted);
        Assert.False(ownerView.Pinned);
        Assert.True(memberView.Muted);
        Assert.False(memberView.Pinned);
        Assert.Equal(2, memberView.Members.Count);
    }
}
