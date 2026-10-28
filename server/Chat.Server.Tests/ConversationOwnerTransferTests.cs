using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// F-24：群主转让单测（角色互换、owner.transferred 系统消息、权限与校验、按收件人视角的会话视图）。
/// 契约见 protocol/protocol.md §4.3（系统消息事件）与 §4.5（POST /api/conversations/{id}/owner）。
/// </summary>
public sealed class ConversationOwnerTransferTests
{
    [Fact]
    public async Task TransferOwnerAsync_Succeeds_SwapsRolesAndWritesSystemMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner1@example.com", "群主甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member1@example.com", "成员乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var result = await service.TransferOwnerAsync(owner.Id, conversation.Id, member.Id, CancellationToken.None);

        // 角色已在库里互换。
        var roles = await db.ConversationMembers.AsNoTracking()
            .Where(item => item.ConversationId == conversation.Id)
            .ToDictionaryAsync(item => item.UserId, item => item.Role);
        Assert.Equal(MemberRole.Member, roles[owner.Id]);
        Assert.Equal(MemberRole.Owner, roles[member.Id]);

        // 系统消息：event/actor/target/text 均按协议 §4.3。
        var systemMessage = result.SystemMessage;
        Assert.Equal(MessageType.System, systemMessage.MsgType);
        var content = systemMessage.Content.As<SystemContent>()!;
        Assert.Equal("owner.transferred", content.Event);
        Assert.Equal(owner.Id, content.ActorId);
        Assert.Equal(new[] { member.Id }, content.TargetUserIds!.ToArray());
        Assert.Contains("群主甲", content.Text);
        Assert.Contains("成员乙", content.Text);

        // 返回的成员集合包含全部成员（供 conv.updated 逐收件人推送）。
        Assert.Equal(2, result.MemberUserIds.Count);
        Assert.Contains(owner.Id, result.MemberUserIds);
        Assert.Contains(member.Id, result.MemberUserIds);
    }

    [Fact]
    public async Task TransferOwnerAsync_SystemMessageSeqIsNextAndUpdatesConversationLastSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner2@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member2@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var result = await service.TransferOwnerAsync(owner.Id, conversation.Id, member.Id, CancellationToken.None);

        // 种子群 lastSeq=0，系统消息 seq 严格 +1。
        Assert.Equal(1, result.SystemMessage.Seq);
        var stored = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Equal(1, stored.LastSeq);
        Assert.Equal(result.SystemMessage.MessageId, stored.LastMessageId);
    }

    [Fact]
    public async Task TransferOwnerAsync_BuildsPerRecipientViewsWithSwappedRoles()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner3@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member3@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        var result = await service.TransferOwnerAsync(owner.Id, conversation.Id, member.Id, CancellationToken.None);
        var perRecipient = await service.BuildPerRecipientAsync(
            result.Conversation, result.MemberUserIds, CancellationToken.None);

        Assert.Equal(2, perRecipient.Count);
        var ownerView = perRecipient[owner.Id];
        Assert.Equal(MemberRole.Member, ownerView.Members.Single(item => item.UserId == owner.Id).Role);
        Assert.Equal(MemberRole.Owner, ownerView.Members.Single(item => item.UserId == member.Id).Role);

        // 新群主视角同样能看到互换后的角色。
        var memberView = perRecipient[member.Id];
        Assert.Equal(MemberRole.Owner, memberView.Members.Single(item => item.UserId == member.Id).Role);
    }

    [Fact]
    public async Task TransferOwnerAsync_EmptyNewOwnerId_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner4@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member4@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.TransferOwnerAsync(owner.Id, conversation.Id, "  ", CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TransferOwnerAsync_ConversationNotFound_IsNotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner5@example.com", "甲");
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.TransferOwnerAsync(owner.Id, Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), CancellationToken.None),
            StatusCodes.Status404NotFound);
    }

    [Fact]
    public async Task TransferOwnerAsync_DirectConversation_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner6@example.com", "甲");
        var other = await TestSupport.SeedUserAsync(db, "ot-other6@example.com", "乙");
        var direct = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.TransferOwnerAsync(owner.Id, direct.Id, other.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TransferOwnerAsync_Outsider_IsNotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner7@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member7@example.com", "乙");
        var outsider = await TestSupport.SeedUserAsync(db, "ot-outsider7@example.com", "丙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.TransferOwnerAsync(outsider.Id, conversation.Id, member.Id, CancellationToken.None),
            StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TransferOwnerAsync_NonOwnerMember_IsForbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner8@example.com", "甲");
        var memberB = await TestSupport.SeedUserAsync(db, "ot-b8@example.com", "乙");
        var memberC = await TestSupport.SeedUserAsync(db, "ot-c8@example.com", "丙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, memberB, memberC);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.TransferOwnerAsync(memberB.Id, conversation.Id, memberC.Id, CancellationToken.None),
            StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task TransferOwnerAsync_ToSelf_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner9@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member9@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.TransferOwnerAsync(owner.Id, conversation.Id, owner.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TransferOwnerAsync_TargetNotInGroup_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner10@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member10@example.com", "乙");
        var outsider = await TestSupport.SeedUserAsync(db, "ot-outsider10@example.com", "丙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.TransferOwnerAsync(owner.Id, conversation.Id, outsider.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TransferOwnerAsync_UnknownTargetUser_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner11@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member11@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.TransferOwnerAsync(owner.Id, conversation.Id, Guid.NewGuid().ToString("D"), CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task TransferOwnerAsync_OldOwnerCannotUpdateProfile_NewOwnerCan()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner12@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member12@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await service.TransferOwnerAsync(owner.Id, conversation.Id, member.Id, CancellationToken.None);

        // 原群主失去群资料修改权限。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.UpdateGroupProfileAsync(owner.Id, conversation.Id, "新群名", null, null, CancellationToken.None),
            StatusCodes.Status403Forbidden);

        // 新群主可修改群资料。
        var updated = await service.UpdateGroupProfileAsync(member.Id, conversation.Id, "新群名", null, null, CancellationToken.None);
        Assert.Equal("新群名", updated.Conversation.Title);
    }

    [Fact]
    public async Task TransferOwnerAsync_TransferBack_Succeeds()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ot-owner13@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ot-member13@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await service.TransferOwnerAsync(owner.Id, conversation.Id, member.Id, CancellationToken.None);
        var second = await service.TransferOwnerAsync(member.Id, conversation.Id, owner.Id, CancellationToken.None);

        var roles = await db.ConversationMembers.AsNoTracking()
            .Where(item => item.ConversationId == conversation.Id)
            .ToDictionaryAsync(item => item.UserId, item => item.Role);
        Assert.Equal(MemberRole.Owner, roles[owner.Id]);
        Assert.Equal(MemberRole.Member, roles[member.Id]);

        // 第二次转让也写系统消息，seq 继续 +1。
        Assert.Equal(2, second.SystemMessage.Seq);
        Assert.Equal("owner.transferred", second.SystemMessage.Content.As<SystemContent>()!.Event);
    }
}
