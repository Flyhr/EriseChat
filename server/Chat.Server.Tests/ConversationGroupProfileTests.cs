using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-14：群资料（群名 + 群公告）更新与校验单测。
/// 契约见 protocol/protocol.md §4.5（announcement ≤500、null=无公告；conv.updated 按收件人视角）。
/// </summary>
public sealed class ConversationGroupProfileTests
{
    [Fact]
    public async Task UpdateGroupProfileAsync_Rename_KeepsAnnouncement()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner1@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member1@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        conversation.Announcement = "旧公告";
        await db.SaveChangesAsync();
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: "  新群名  ", announcement: null, avatarFileId: null, CancellationToken.None);

        Assert.Equal("新群名", updated.Conversation.Title);
        Assert.Equal("旧公告", updated.Conversation.Announcement);
        Assert.Contains(member.Id, updated.MemberUserIds);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_SetAnnouncement_PersistsAndReturnedByGet()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner2@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member2@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: null, announcement: "  欢迎加入  ", avatarFileId: null, CancellationToken.None);
        Assert.Equal("欢迎加入", updated.Conversation.Announcement);

        var fetched = await service.GetAsync(member.Id, conversation.Id, CancellationToken.None);
        Assert.Equal("欢迎加入", fetched.Announcement);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_ClearAnnouncement_WithEmptyString()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner3@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member3@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await service.UpdateGroupProfileAsync(owner.Id, conversation.Id, null, "有公告", avatarFileId: null, CancellationToken.None);
        var updated = await service.UpdateGroupProfileAsync(owner.Id, conversation.Id, null, "   ", avatarFileId: null, CancellationToken.None);

        Assert.Null(updated.Conversation.Announcement);
        var row = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Null(row.Announcement);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_NormalizesCrlfToLf()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner4@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member4@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, null, "第一行\r\n第二行\r第三行", avatarFileId: null, CancellationToken.None);

        Assert.Equal("第一行\n第二行\n第三行", updated.Conversation.Announcement);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_UpdateBothFieldsAtOnce()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner5@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member5@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(owner.Id, conversation.Id, "群名A", "公告A", avatarFileId: null, CancellationToken.None);

        Assert.Equal("群名A", updated.Conversation.Title);
        Assert.Equal("公告A", updated.Conversation.Announcement);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_NonOwner_Forbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner6@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member6@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.UpdateGroupProfileAsync(member.Id, conversation.Id, "非群主改名", null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_NotMember_NotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-owner7@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-member7@example.com", "乙");
        var outsider = await TestSupport.SeedUserAsync(db, "gp-out7@example.com", "丙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.UpdateGroupProfileAsync(outsider.Id, conversation.Id, "改名", null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_DirectConversation_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o8@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m8@example.com", "乙");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, "单聊改名", null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_ConversationNotFound_NotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o9@example.com", "甲");
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.UpdateGroupProfileAsync(owner.Id, Guid.NewGuid().ToString("D"), "改名", null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_EmptyRequest_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o10@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m10@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, null, null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData("", "群聊名称不能为空")]
    [InlineData("   ", "群聊名称不能为空")]
    public async Task UpdateGroupProfileAsync_BlankTitle_ValidationFailed(string title, string expectedMessage)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o11@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m11@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var exception = await Assert.ThrowsAsync<ApiException>(
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, title, null, avatarFileId: null, CancellationToken.None));
        Assert.Equal(ErrorCodes.ValidationFailed, exception.Code);
        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_TitleTooLong_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o12@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m12@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, new string('名', 101), null, avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_AnnouncementTooLong_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o13@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m13@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, null, new string('公', 501), avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateGroupProfileAsync_AnnouncementControlCharacter_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o14@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m14@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, null, "公告\t含制表符", avatarFileId: null, CancellationToken.None),
            expectedStatus: StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task BuildPerRecipientAsync_ReturnsPerRecipientPinnedAndAnnouncement()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o15@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m15@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await service.UpdateGroupProfileAsync(owner.Id, conversation.Id, "群名", "公告", avatarFileId: null, CancellationToken.None);
        await service.UpdateMemberSettingsAsync(member.Id, conversation.Id, pinned: true, muted: null, CancellationToken.None);

        var perRecipient = await service.BuildPerRecipientAsync(
            conversation,
            new[] { owner.Id, member.Id },
            CancellationToken.None);

        Assert.False(perRecipient[owner.Id].Pinned);
        Assert.True(perRecipient[member.Id].Pinned);
        Assert.Equal("公告", perRecipient[owner.Id].Announcement);
        Assert.Equal("公告", perRecipient[member.Id].Announcement);
    }

    [Fact]
    public async Task GetAsync_OldConversationWithoutAnnouncement_ReturnsNull()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "gp-o16@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "gp-m16@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var fetched = await service.GetAsync(owner.Id, conversation.Id, CancellationToken.None);
        Assert.Null(fetched.Announcement);

        var list = await service.ListAsync(owner.Id, CancellationToken.None);
        Assert.Null(list.Single(item => item.ConversationId == conversation.Id).Announcement);
    }
}
