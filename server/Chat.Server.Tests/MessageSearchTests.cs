using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Data;
using Chat.Server.Modules.Messages;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// F-11 单测：消息搜索（REST，非协议帧）。
/// 覆盖本人会话可见性、中文命中、ASCII 大小写不敏感、参数校验、会话成员校验、
/// beforeSeq 翻页与 hasMore、仅 text 类型、seq 降序、含引号/反斜杠的文本不抛异常。
/// </summary>
public sealed class MessageSearchTests
{
    [Fact]
    public async Task SearchAsync_OwnConversation_ReturnsTextMessage()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-own-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-own-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            alice.Id,
            NewText(conversation.Id, "今天开会了吗？"),
            CancellationToken.None);

        var page = await service.SearchAsync(bob.Id, "开会", null, null, null, CancellationToken.None);

        var hit = Assert.Single(page.Messages);
        Assert.Equal(sent.Message.MessageId, hit.MessageId);
        Assert.Equal(conversation.Id, hit.ConversationId);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task SearchAsync_DoesNotReturnOtherUsersConversationMessages()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-privacy-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-privacy-b@example.com");
        var carol = await TestSupport.SeedUserAsync(db, "search-privacy-c@example.com");
        var mine = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var theirs = await TestSupport.SeedDirectConversationAsync(db, bob, carol);
        var service = TestSupport.CreateMessageService(db);

        await service.SendAsync(alice.Id, NewText(mine.Id, "我的可见消息"), CancellationToken.None);
        await service.SendAsync(bob.Id, NewText(theirs.Id, "别人的私有消息"), CancellationToken.None);

        var page = await service.SearchAsync(alice.Id, "消息", null, null, null, CancellationToken.None);

        var hit = Assert.Single(page.Messages);
        Assert.Equal(mine.Id, hit.ConversationId);
    }

    [Fact]
    public async Task SearchAsync_ChineseKeyword_Matches()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-cn-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-cn-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            bob.Id,
            NewText(conversation.Id, "项目上线计划已经确认，请查收"),
            CancellationToken.None);

        var page = await service.SearchAsync(alice.Id, "上线计划", conversation.Id, null, null, CancellationToken.None);

        Assert.Equal(sent.Message.MessageId, Assert.Single(page.Messages).MessageId);
    }

    [Fact]
    public async Task SearchAsync_AsciiKeyword_IsCaseInsensitive()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-case-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-case-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            alice.Id,
            NewText(conversation.Id, "Please review the Deploy Plan"),
            CancellationToken.None);

        var lower = await service.SearchAsync(alice.Id, "deploy plan", null, null, null, CancellationToken.None);
        var upper = await service.SearchAsync(alice.Id, "DEPLOY PLAN", null, null, null, CancellationToken.None);

        Assert.Equal(sent.Message.MessageId, Assert.Single(lower.Messages).MessageId);
        Assert.Equal(sent.Message.MessageId, Assert.Single(upper.Messages).MessageId);
    }

    [Fact]
    public async Task SearchAsync_EmptyOrTooLongQuery_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-invalid-a@example.com");
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SearchAsync(alice.Id, "   ", null, null, null, CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SearchAsync(
                alice.Id,
                new string('a', MessageService.SearchQueryMaxLength + 1),
                null,
                null,
                null,
                CancellationToken.None),
            400);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SearchAsync(alice.Id, "ok", null, null, 0, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task SearchAsync_ConversationIdNonMember_NotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-member-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-member-b@example.com");
        var carol = await TestSupport.SeedUserAsync(db, "search-member-c@example.com");
        var theirs = await TestSupport.SeedDirectConversationAsync(db, bob, carol);
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.SearchAsync(alice.Id, "消息", theirs.Id, null, null, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task SearchAsync_BeforeSeq_PaginatesWithHasMore()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-page-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-page-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        for (var index = 1; index <= 5; index++)
        {
            await service.SendAsync(alice.Id, NewText(conversation.Id, $"分页测试消息 {index}"), CancellationToken.None);
        }

        var first = await service.SearchAsync(alice.Id, "分页测试", conversation.Id, null, 2, CancellationToken.None);
        Assert.Equal(2, first.Messages.Count);
        Assert.True(first.HasMore);
        Assert.Equal(5, first.Messages[0].Seq);
        Assert.Equal(4, first.Messages[1].Seq);

        var second = await service.SearchAsync(alice.Id, "分页测试", conversation.Id, 4, 2, CancellationToken.None);
        Assert.Equal(2, second.Messages.Count);
        Assert.True(second.HasMore);
        Assert.Equal(3, second.Messages[0].Seq);
        Assert.Equal(2, second.Messages[1].Seq);

        var last = await service.SearchAsync(alice.Id, "分页测试", conversation.Id, 2, 5, CancellationToken.None);
        var single = Assert.Single(last.Messages);
        Assert.Equal(1, single.Seq);
        Assert.False(last.HasMore);
    }

    [Fact]
    public async Task SearchAsync_OnlyReturnsTextMessages()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-type-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-type-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(alice.Id, NewText(conversation.Id, "唯一文本命中"), CancellationToken.None);
        SeedNonTextMessage(db, conversation.Id, alice.Id, 2, MessageType.Image, "{\"fileId\":\"f1\",\"name\":\"唯一文本命中.png\",\"size\":1,\"mime\":\"image/png\"}");
        SeedNonTextMessage(db, conversation.Id, alice.Id, 3, MessageType.File, "{\"fileId\":\"f2\",\"name\":\"唯一文本命中.pdf\",\"size\":1,\"mime\":\"application/pdf\"}");
        SeedNonTextMessage(db, conversation.Id, alice.Id, 4, MessageType.System, "{\"event\":\"group.created\",\"text\":\"唯一文本命中 创建了群聊\"}");
        await db.SaveChangesAsync();

        var page = await service.SearchAsync(alice.Id, "唯一文本命中", conversation.Id, null, null, CancellationToken.None);

        Assert.Equal(sent.Message.MessageId, Assert.Single(page.Messages).MessageId);
    }

    [Fact]
    public async Task SearchAsync_ResultsOrderedBySeqDescending()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-order-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-order-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        for (var index = 1; index <= 3; index++)
        {
            await service.SendAsync(alice.Id, NewText(conversation.Id, $"顺序命中 {index}"), CancellationToken.None);
        }

        var page = await service.SearchAsync(alice.Id, "顺序命中", null, null, null, CancellationToken.None);

        Assert.Equal(3, page.Messages.Count);
        Assert.Equal(new long[] { 3, 2, 1 }, page.Messages.Select(message => message.Seq).ToArray());
    }

    [Fact]
    public async Task SearchAsync_TextWithQuotesAndBackslashes_DoesNotThrow()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var alice = await TestSupport.SeedUserAsync(db, "search-escape-a@example.com");
        var bob = await TestSupport.SeedUserAsync(db, "search-escape-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, alice, bob);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            alice.Id,
            NewText(conversation.Id, "他说：\"你好\"，路径 C:\\temp\\file.txt"),
            CancellationToken.None);

        var byQuote = await service.SearchAsync(alice.Id, "你好", null, null, null, CancellationToken.None);
        var byBackslash = await service.SearchAsync(alice.Id, "temp", null, null, null, CancellationToken.None);

        Assert.Equal(sent.Message.MessageId, Assert.Single(byQuote.Messages).MessageId);
        Assert.Equal(sent.Message.MessageId, Assert.Single(byBackslash.Messages).MessageId);
    }

    private static MessageSendFrame NewText(string conversationId, string text) =>
        new(
            Guid.NewGuid().ToString("N"),
            conversationId,
            MessageType.Text,
            MessageContent.FromString(text));

    /// <summary>直接落库一条非文本消息（绕过发送校验，用于验证搜索只返回 text）。</summary>
    private static void SeedNonTextMessage(
        ChatDbContext db,
        string conversationId,
        string senderId,
        long seq,
        MessageType msgType,
        string contentJson)
    {
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversationId,
            SenderId = senderId,
            ClientMsgId = Guid.NewGuid().ToString("N"),
            Seq = seq,
            MsgType = msgType,
            ContentJson = contentJson,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }
}
