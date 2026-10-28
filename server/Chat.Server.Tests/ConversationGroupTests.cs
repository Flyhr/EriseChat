using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：群聊成员管理单测（建群、邀请、退出/移除、系统消息、权限与上限）。
/// 覆盖 M3-04 服务端行为，契约口径以 protocol/protocol.md §4.3/§4.5/§4.6 与计划 §4.6 为准。
/// </summary>
public sealed class ConversationGroupTests
{
    [Fact]
    public async Task CreateGroupAsync_WritesCreatedSystemMessageAndMembers()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "g-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "g-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "g-c@example.com", "成员丙");
        var service = TestSupport.CreateConversationService(db);

        var result = await service.CreateGroupAsync(
            owner.Id,
            "  测试群  ",
            new[] { memberB.Id, memberC.Id },
            CancellationToken.None);

        // 会话 DTO：group、群名已 trim、lastSeq=1（group.created 系统消息）、直接键为空。
        Assert.Equal(ConversationType.Group, result.Conversation.Type);
        Assert.Equal("测试群", result.Conversation.Title);
        Assert.Equal(1, result.Conversation.LastSeq);
        Assert.Equal(3, result.Conversation.Members.Count);
        Assert.Equal(3, result.MemberUserIds.Count);
        Assert.Contains(owner.Id, result.MemberUserIds);
        Assert.Null(result.RemovedUserId);

        // 系统消息：seq=1、msg_type=system、content 为 group.created 事件对象。
        Assert.NotNull(result.SystemMessage);
        var systemMessage = result.SystemMessage!;
        Assert.Equal(1, systemMessage.Seq);
        Assert.Equal(MessageType.System, systemMessage.MsgType);
        Assert.True(systemMessage.Content.IsObject);
        var content = systemMessage.Content.As<SystemContent>()!;
        Assert.Equal("group.created", content.Event);
        Assert.Equal(owner.Id, content.ActorId);
        Assert.Null(content.TargetUserIds);

        // 落库：last_seq/last_message_id 指向系统消息；direct_key 为 NULL；成员角色 owner/member。
        var stored = await db.Conversations.AsNoTracking()
            .SingleAsync(conversation => conversation.Id == result.Conversation.ConversationId);
        Assert.Equal(1, stored.LastSeq);
        Assert.Equal(systemMessage.MessageId, stored.LastMessageId);
        Assert.Null(stored.DirectKey);

        var members = await db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == stored.Id)
            .ToListAsync();
        Assert.Equal(3, members.Count);
        Assert.Equal(MemberRole.Owner, members.Single(member => member.UserId == owner.Id).Role);
        Assert.Equal(MemberRole.Member, members.Single(member => member.UserId == memberB.Id).Role);
        Assert.Equal(MemberRole.Member, members.Single(member => member.UserId == memberC.Id).Role);
        Assert.All(members, member => Assert.Equal(0, member.LastReadSeq));

        var storedMessage = await db.Messages.AsNoTracking()
            .SingleAsync(message => message.ConversationId == stored.Id);
        Assert.Equal(MessageType.System, storedMessage.MsgType);
        Assert.Null(storedMessage.FileId);
    }

    [Fact]
    public async Task AddMembersAsync_UsesJoinSeqAsLastReadSeqAndWritesJoinedMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "inv-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "inv-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "inv-c@example.com", "成员丙");
        var memberD = await TestSupport.SeedUserAsync(db, "inv-d@example.com", "成员丁");
        var conversationService = TestSupport.CreateConversationService(db);
        var messageService = TestSupport.CreateMessageService(db);

        var group = await conversationService.CreateGroupAsync(
            owner.Id, "邀请群", new[] { memberB.Id, memberC.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        // 群内先发一条普通消息，把 lastSeq 推到 2（清跟踪态，模拟独立请求的作用域 DbContext）。
        db.ChangeTracker.Clear();
        var sent = await messageService.SendAsync(owner.Id, NewText(conversationId, "欢迎"), CancellationToken.None);
        Assert.Equal(2, sent.Message.Seq);

        db.ChangeTracker.Clear();
        var result = await conversationService.AddMembersAsync(
            owner.Id, conversationId, new[] { memberD.Id }, CancellationToken.None);

        // member.joined 系统消息 seq=3；新成员 last_read_seq=加入前 lastSeq(2)（不是 0，也不是 3）。
        Assert.NotNull(result.SystemMessage);
        var systemMessage = result.SystemMessage!;
        Assert.Equal(3, systemMessage.Seq);
        Assert.Equal(3, result.Conversation.LastSeq);
        Assert.Equal(systemMessage.MessageId, result.Conversation.LastMessage?.MessageId);
        Assert.Equal(4, result.Conversation.Members.Count);
        Assert.Contains(memberD.Id, result.MemberUserIds);

        var joined = systemMessage.Content.As<SystemContent>()!;
        Assert.Equal("member.joined", joined.Event);
        Assert.Equal(owner.Id, joined.ActorId);
        Assert.Equal(new[] { memberD.Id }, joined.TargetUserIds!.ToArray());
        Assert.Contains("邀请", joined.Text);

        var membership = await db.ConversationMembers.AsNoTracking()
            .SingleAsync(member => member.ConversationId == conversationId && member.UserId == memberD.Id);
        Assert.Equal(2, membership.LastReadSeq);
        Assert.Equal(MemberRole.Member, membership.Role);
    }

    [Fact]
    public async Task AddMembersAsync_AlreadyInGroup_IsIdempotentWithoutSystemMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "idem-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "idem-b@example.com", "成员乙");
        var service = TestSupport.CreateConversationService(db);

        var group = await service.CreateGroupAsync(
            owner.Id, "幂等群", new[] { memberB.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        // 重复邀请已在群成员（并附带自己），无新增时不写系统消息、不推进 lastSeq。
        var result = await service.AddMembersAsync(
            owner.Id, conversationId, new[] { memberB.Id, owner.Id }, CancellationToken.None);

        Assert.Null(result.SystemMessage);
        Assert.Equal(group.Conversation.LastSeq, result.Conversation.LastSeq);
        Assert.Equal(2, result.Conversation.Members.Count);
        var messageCount = await db.Messages.AsNoTracking()
            .CountAsync(message => message.ConversationId == conversationId);
        Assert.Equal(1, messageCount);
    }

    [Fact]
    public async Task AddMembersAsync_ChecksMembershipAndOwnerOnlyOption()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "perm-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "perm-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "perm-c@example.com", "成员丙");
        var outsider = await TestSupport.SeedUserAsync(db, "perm-out@example.com", "群外用户");
        var service = TestSupport.CreateConversationService(db);

        var group = await service.CreateGroupAsync(
            owner.Id, "权限群", new[] { memberB.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        // 非成员邀请 → NOT_MEMBER。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.AddMembersAsync(outsider.Id, conversationId, new[] { memberC.Id }, CancellationToken.None),
            403);

        // 默认 GroupOnlyOwnerCanInvite=false：普通成员可邀请。
        var byMember = await service.AddMembersAsync(
            memberB.Id, conversationId, new[] { memberC.Id }, CancellationToken.None);
        Assert.NotNull(byMember.SystemMessage);
        Assert.Equal("member.joined", byMember.SystemMessage!.Content.As<SystemContent>()!.Event);

        // GroupOnlyOwnerCanInvite=true：普通成员邀请 → FORBIDDEN。
        var memberD = await TestSupport.SeedUserAsync(db, "perm-d@example.com", "成员丁");
        var strictService = TestSupport.CreateConversationService(
            db, new AppOptions { GroupOnlyOwnerCanInvite = true });
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => strictService.AddMembersAsync(memberB.Id, conversationId, new[] { memberD.Id }, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task CreateGroupAsync_And_AddMembersAsync_EnforceMaxMembers()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "max-owner@example.com");
        var memberB = await TestSupport.SeedUserAsync(db, "max-b@example.com");
        var memberC = await TestSupport.SeedUserAsync(db, "max-c@example.com");
        var memberD = await TestSupport.SeedUserAsync(db, "max-d@example.com");
        var service = TestSupport.CreateConversationService(db, new AppOptions { GroupMaxMembers = 3 });

        // 上限 3（含群主）：owner + B + C 恰好 3 人，成功。
        var group = await service.CreateGroupAsync(
            owner.Id, "三人群", new[] { memberB.Id, memberC.Id }, CancellationToken.None);

        // 满员再邀请 → VALIDATION_FAILED。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.AddMembersAsync(owner.Id, group.Conversation.ConversationId, new[] { memberD.Id }, CancellationToken.None),
            400);

        // 建群即超限 → VALIDATION_FAILED。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.CreateGroupAsync(owner.Id, "四人群", new[] { memberB.Id, memberC.Id, memberD.Id }, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task RemoveMemberAsync_RemovesMemberAndBlocksFurtherSend()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "rm-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "rm-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "rm-c@example.com", "成员丙");
        var conversationService = TestSupport.CreateConversationService(db);
        var messageService = TestSupport.CreateMessageService(db);

        var group = await conversationService.CreateGroupAsync(
            owner.Id, "移除群", new[] { memberB.Id, memberC.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        var result = await conversationService.RemoveMemberAsync(
            owner.Id, conversationId, memberB.Id, CancellationToken.None);

        // 系统消息 seq+1（1 → 2）、member.removed、目标用户写在 targetUserIds。
        Assert.NotNull(result.SystemMessage);
        var systemMessage = result.SystemMessage!;
        Assert.Equal(2, systemMessage.Seq);
        var removed = systemMessage.Content.As<SystemContent>()!;
        Assert.Equal("member.removed", removed.Event);
        Assert.Equal(new[] { memberB.Id }, removed.TargetUserIds!.ToArray());
        Assert.Equal(memberB.Id, result.RemovedUserId);
        Assert.Equal(2, result.Conversation.Members.Count);
        Assert.Equal(2, result.Conversation.LastSeq);

        Assert.Null(await db.ConversationMembers.AsNoTracking()
            .FirstOrDefaultAsync(member => member.ConversationId == conversationId && member.UserId == memberB.Id));

        // 被移除者再发消息 → NOT_MEMBER。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => messageService.SendAsync(memberB.Id, NewText(conversationId, "我还在吗"), CancellationToken.None),
            403);
    }

    [Fact]
    public async Task RemoveMemberAsync_RejectsNonOwnerOwnerSelfAndUnknownTarget()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "rmp-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "rmp-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "rmp-c@example.com", "成员丙");
        var outsider = await TestSupport.SeedUserAsync(db, "rmp-out@example.com", "群外用户");
        var service = TestSupport.CreateConversationService(db);

        var group = await service.CreateGroupAsync(
            owner.Id, "权限群", new[] { memberB.Id, memberC.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        // 非群主移除他人 → FORBIDDEN。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.RemoveMemberAsync(memberB.Id, conversationId, memberC.Id, CancellationToken.None),
            403);

        // 群主移除自己（退出）→ VALIDATION_FAILED（F-24 起群主需先转让群主）。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.RemoveMemberAsync(owner.Id, conversationId, owner.Id, CancellationToken.None),
            400);

        // 目标是群外用户 → NOT_FOUND「该用户不是群成员」。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.RemoveMemberAsync(owner.Id, conversationId, outsider.Id, CancellationToken.None),
            404);

        // 非群聊会话的成员管理 → VALIDATION_FAILED（单聊）。
        var other = await TestSupport.SeedUserAsync(db, "rmp-other@example.com", "单聊对方");
        var direct = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.RemoveMemberAsync(owner.Id, direct.Id, other.Id, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task RemoveMemberAsync_SelfLeave_WritesLeftMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "left-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "left-b@example.com", "成员乙");
        var memberC = await TestSupport.SeedUserAsync(db, "left-c@example.com", "成员丙");
        var service = TestSupport.CreateConversationService(db);

        var group = await service.CreateGroupAsync(
            owner.Id, "退出群", new[] { memberB.Id, memberC.Id }, CancellationToken.None);
        var conversationId = group.Conversation.ConversationId;

        var result = await service.RemoveMemberAsync(memberC.Id, conversationId, memberC.Id, CancellationToken.None);

        Assert.NotNull(result.SystemMessage);
        var systemMessage = result.SystemMessage!;
        Assert.Equal(2, systemMessage.Seq);
        var content = systemMessage.Content.As<SystemContent>()!;
        Assert.Equal("member.left", content.Event);
        Assert.Equal(memberC.Id, content.ActorId);
        Assert.Null(content.TargetUserIds);
        Assert.Contains("退出", content.Text);
        Assert.Equal(memberC.Id, result.RemovedUserId);
        Assert.Equal(2, result.Conversation.Members.Count);

        // 群主退出 → VALIDATION_FAILED。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.RemoveMemberAsync(owner.Id, conversationId, owner.Id, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task CreateGroupAsync_ValidatesTitleMembersAndAvailability()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "val-owner@example.com", "群主甲");
        var memberB = await TestSupport.SeedUserAsync(db, "val-b@example.com", "成员乙");
        var disabled = await TestSupport.SeedUserAsync(db, "val-disabled@example.com", "禁用用户");
        disabled.Status = UserStatus.Disabled;
        await db.SaveChangesAsync();
        var service = TestSupport.CreateConversationService(db);

        // 群名必填、不能全空白。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.CreateGroupAsync(owner.Id, "   ", new[] { memberB.Id }, CancellationToken.None),
            400);

        // 至少邀请 1 位成员（不含群主）。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.CreateGroupAsync(owner.Id, "群", Array.Empty<string>(), CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.CreateGroupAsync(owner.Id, "群", new[] { owner.Id }, CancellationToken.None),
            400);

        // 不存在的成员 → NOT_FOUND；禁用成员 → NOT_FOUND。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.CreateGroupAsync(
                owner.Id, "群", new[] { memberB.Id, Guid.NewGuid().ToString("D") }, CancellationToken.None),
            404);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.CreateGroupAsync(owner.Id, "群", new[] { disabled.Id }, CancellationToken.None),
            404);
    }

    private static MessageSendFrame NewText(string conversationId, string text) =>
        new(
            "c-" + Guid.NewGuid().ToString("N"),
            conversationId,
            MessageType.Text,
            MessageContent.FromString(text));
}
