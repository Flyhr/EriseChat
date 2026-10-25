using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Modules.Messages;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// M1-09：消息核心单测。覆盖 seq 严格 +1 与并发唯一、clientMsgId 幂等、sync 分页/hasMore、
/// REST 历史分页（真实 SQLite 临时库 + 真实迁移）。
/// </summary>
public sealed class MessageCoreTests
{
    [Fact]
    public async Task SendAsync_AssignsStrictlyIncreasingSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "seq-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "seq-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        for (var index = 1; index <= 5; index++)
        {
            var result = await service.SendAsync(sender.Id, NewSend(conversation.Id, $"text-{index}"), CancellationToken.None);
            Assert.True(result.Created);
            Assert.Equal(index, result.Message.Seq);
            Assert.Equal(2, result.MemberUserIds.Count);
        }

        var persisted = await db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == conversation.Id)
            .OrderBy(message => message.Seq)
            .Select(message => message.Seq)
            .ToListAsync();
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, persisted);

        var storedConversation = await db.Conversations.AsNoTracking().FirstAsync(item => item.Id == conversation.Id);
        Assert.Equal(5, storedConversation.LastSeq);
        Assert.NotNull(storedConversation.LastMessageId);
    }

    [Fact]
    public async Task SendAsync_DuplicateClientMsgId_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "dup-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "dup-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);
        var request = NewSend(conversation.Id, "same-content");

        var first = await service.SendAsync(sender.Id, request, CancellationToken.None);
        var second = await service.SendAsync(sender.Id, request, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Message.MessageId, second.Message.MessageId);
        Assert.Equal(first.Message.Seq, second.Message.Seq);
        Assert.Equal(1, await db.Messages.CountAsync(message => message.ConversationId == conversation.Id));
        Assert.Equal(1, (await db.Conversations.AsNoTracking().FirstAsync(item => item.Id == conversation.Id)).LastSeq);
    }

    [Fact]
    public async Task SendAsync_ConcurrentSends_ProduceUniqueSequentialSeqs()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var seedDb = database.CreateContext())
        {
            var sender = await TestSupport.SeedUserAsync(seedDb, "concurrent-a@example.com");
            var other = await TestSupport.SeedUserAsync(seedDb, "concurrent-b@example.com");
            var conversation = await TestSupport.SeedDirectConversationAsync(seedDb, sender, other);

            // 每条并发发送使用独立 DbContext/连接（同一 SQLite 文件库），模拟真实多连接并发写入。
            var tasks = Enumerable.Range(0, 10).Select(async index =>
            {
                await using var context = database.CreateContext();
                var service = TestSupport.CreateMessageService(context);
                var senderId = index % 2 == 0 ? sender.Id : other.Id;
                return await service.SendAsync(
                    senderId,
                    NewSend(conversation.Id, $"concurrent-{index}"),
                    CancellationToken.None);
            }).ToList();

            var results = await Task.WhenAll(tasks);

            var seqs = results.Select(result => result.Message.Seq).OrderBy(seq => seq).ToArray();
            Assert.Equal(Enumerable.Range(1, 10).Select(value => (long)value).ToArray(), seqs);
            Assert.All(results, result => Assert.True(result.Created));

            var stored = await seedDb.Messages.AsNoTracking()
                .Where(message => message.ConversationId == conversation.Id)
                .Select(message => message.Seq)
                .ToListAsync();
            Assert.Equal(10, stored.Count);
            Assert.Equal(10, stored.Distinct().Count());
            Assert.Equal(10, (await seedDb.Conversations.AsNoTracking().FirstAsync(item => item.Id == conversation.Id)).LastSeq);
        }
    }

    [Fact]
    public async Task SendAsync_NonMember_ThrowsNotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "member-owner@example.com");
        var member = await TestSupport.SeedUserAsync(db, "member-target@example.com");
        var stranger = await TestSupport.SeedUserAsync(db, "member-stranger@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, member);
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.SendAsync(stranger.Id, NewSend(conversation.Id, "intruder"), CancellationToken.None),
            expectedStatus: 403);
    }

    [Fact]
    public async Task SyncAsync_PagesAtTwoHundredAndReportsHasMore()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "sync-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "sync-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, count: 250);
        var service = TestSupport.CreateMessageService(db);

        var firstPage = await service.SyncAsync(
            sender.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 0) }),
            CancellationToken.None);
        Assert.True(firstPage.HasMore);
        var firstBatch = Assert.Single(firstPage.Batches);
        Assert.Equal(200, firstBatch.Messages.Count);
        Assert.Equal(1, firstBatch.Messages[0].Seq);
        Assert.Equal(200, firstBatch.Messages[^1].Seq);

        var secondPage = await service.SyncAsync(
            sender.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 200) }),
            CancellationToken.None);
        Assert.False(secondPage.HasMore);
        var secondBatch = Assert.Single(secondPage.Batches);
        Assert.Equal(50, secondBatch.Messages.Count);
        Assert.Equal(201, secondBatch.Messages[0].Seq);
        Assert.Equal(250, secondBatch.Messages[^1].Seq);

        var caughtUp = await service.SyncAsync(
            sender.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 250) }),
            CancellationToken.None);
        Assert.False(caughtUp.HasMore);
        Assert.Empty(caughtUp.Batches);
    }

    [Fact]
    public async Task SyncAsync_RejectsCursorBeyondConversationLastSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "sync-range-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "sync-range-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, count: 3);
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SyncAsync(
                sender.Id,
                new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 99) }),
                CancellationToken.None));
    }

    [Fact]
    public async Task GetHistoryAsync_ReturnsAscendingPageAndHasMore()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "history-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "history-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, count: 250);
        var service = TestSupport.CreateMessageService(db);

        var latest = await service.GetHistoryAsync(sender.Id, conversation.Id, beforeSeq: null, limit: null, CancellationToken.None);
        Assert.True(latest.HasMore);
        Assert.Equal(MessageService.HistoryDefaultLimit, latest.Messages.Count);
        Assert.Equal(201, latest.Messages[0].Seq);
        Assert.Equal(250, latest.Messages[^1].Seq);

        var earlier = await service.GetHistoryAsync(sender.Id, conversation.Id, beforeSeq: 201, limit: 50, CancellationToken.None);
        Assert.True(earlier.HasMore);
        Assert.Equal(151, earlier.Messages[0].Seq);
        Assert.Equal(200, earlier.Messages[^1].Seq);

        var oldest = await service.GetHistoryAsync(sender.Id, conversation.Id, beforeSeq: 1, limit: 50, CancellationToken.None);
        Assert.False(oldest.HasMore);
        Assert.Empty(oldest.Messages);
    }

    private static MessageSendFrame NewSend(string conversationId, string text) => new(
        ClientMsgId: Guid.NewGuid().ToString("N"),
        ConversationId: conversationId,
        MsgType: MessageType.Text,
        Content: MessageContent.FromString(text));
}
