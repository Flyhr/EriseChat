using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Messages;
using Microsoft.EntityFrameworkCore;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// F-09 单测：回复引用摘要（protocol/protocol.md §4.3）。
/// 覆盖 ack 回执带摘要、文本/图片/系统摘要字段、replyTo 不存在或跨会话校验、历史与 sync 带摘要、
/// 120 字符截断与幂等重发。
/// </summary>
public sealed class ReplyTests
{
    [Fact]
    public async Task SendAsync_TextReply_ReturnsReplySummaryInAck()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-text-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-text-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var original = await service.SendAsync(sender.Id, NewText(conversation.Id, "原始消息"), CancellationToken.None);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "回复内容", original.Message.MessageId),
            CancellationToken.None);

        Assert.True(reply.Created);
        var summary = reply.Message.Reply;
        Assert.NotNull(summary);
        Assert.Equal(original.Message.MessageId, summary.MessageId);
        Assert.Equal(original.Message.Seq, summary.Seq);
        Assert.Equal(sender.Id, summary.SenderId);
        Assert.Equal(MessageType.Text, summary.MsgType);
        Assert.Equal("原始消息", summary.Text);
        Assert.Null(summary.FileId);
        Assert.Null(summary.Name);
    }

    [Fact]
    public async Task SendAsync_ImageReply_CarriesFileIdAndNameWithoutText()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-image-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-image-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "截图.png", "image/png", 2048);
        var service = TestSupport.CreateMessageService(db);

        var image = await service.SendAsync(
            sender.Id,
            NewMedia(conversation.Id, MessageType.Image, new FileContent(file.Id, file.OriginalName, file.Size, file.Mime)),
            CancellationToken.None);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "这张图不错", image.Message.MessageId),
            CancellationToken.None);

        var summary = reply.Message.Reply;
        Assert.NotNull(summary);
        Assert.Equal(MessageType.Image, summary.MsgType);
        Assert.Equal(file.Id, summary.FileId);
        Assert.Equal("截图.png", summary.Name);
        Assert.Null(summary.Text);
    }

    [Fact]
    public async Task SendAsync_RejectsMissingOrForeignReplyToWithoutAdvancingSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-bad-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-bad-b@example.com");
        var stranger = await TestSupport.SeedUserAsync(db, "reply-bad-c@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var foreignConversation = await TestSupport.SeedDirectConversationAsync(db, other, stranger);
        var service = TestSupport.CreateMessageService(db);

        var original = await service.SendAsync(sender.Id, NewText(conversation.Id, "本会话消息"), CancellationToken.None);
        var foreign = await service.SendAsync(other.Id, NewText(foreignConversation.Id, "别的会话消息"), CancellationToken.None);

        // 不存在的 replyTo。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewText(conversation.Id, "回复不存在", Guid.NewGuid().ToString("D")),
                CancellationToken.None),
            400);

        // 跨会话的 replyTo。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewText(conversation.Id, "回复别的会话", foreign.Message.MessageId),
                CancellationToken.None),
            400);

        // 失败不落库、不推进 last_seq（会话里只有最初那条消息）。
        var stored = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Equal(original.Message.Seq, stored.LastSeq);
        Assert.Equal(
            original.Message.Seq,
            await db.Messages.AsNoTracking().CountAsync(message => message.ConversationId == conversation.Id));
    }

    [Fact]
    public async Task HistoryAndSync_CarryReplySummary()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-hist-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-hist-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        await TestSupport.SeedMessagesAsync(db, conversation.Id, sender.Id, count: 3);
        var service = TestSupport.CreateMessageService(db);

        var replyTarget = await db.Messages.AsNoTracking()
            .SingleAsync(message => message.ConversationId == conversation.Id && message.Seq == 2);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "回复第二条", replyTarget.Id),
            CancellationToken.None);
        Assert.Equal(4, reply.Message.Seq);

        var history = await service.GetHistoryAsync(sender.Id, conversation.Id, beforeSeq: null, limit: null, CancellationToken.None);
        Assert.Equal(4, history.Messages.Count);
        var historyReply = history.Messages.Single(message => message.Seq == 4);
        Assert.NotNull(historyReply.Reply);
        Assert.Equal(2, historyReply.Reply!.Seq);
        Assert.Equal(MessageType.Text, historyReply.Reply.MsgType);
        Assert.Equal("m2", historyReply.Reply.Text);

        var sync = await service.SyncAsync(
            sender.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 0) }),
            CancellationToken.None);
        var batch = Assert.Single(sync.Batches);
        Assert.Equal(4, batch.Messages.Count);
        var syncReply = batch.Messages.Single(message => message.Seq == 4);
        Assert.NotNull(syncReply.Reply);
        Assert.Equal(2, syncReply.Reply!.Seq);
        Assert.Equal("m2", syncReply.Reply.Text);
    }

    [Fact]
    public async Task SendAsync_TruncatesLongReplyTextTo120Characters()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-long-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-long-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var longText = new string('字', 200);
        var original = await service.SendAsync(sender.Id, NewText(conversation.Id, longText), CancellationToken.None);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "回复长消息", original.Message.MessageId),
            CancellationToken.None);

        var summary = reply.Message.Reply;
        Assert.NotNull(summary);
        Assert.NotNull(summary.Text);
        Assert.Equal(MessageService.ReplyTextMaxLength + 1, summary.Text.Length);
        Assert.Equal(new string('字', MessageService.ReplyTextMaxLength) + "…", summary.Text);
    }

    [Fact]
    public async Task SendAsync_IdempotentResendKeepsReplySummary()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-idem-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-idem-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var original = await service.SendAsync(sender.Id, NewText(conversation.Id, "被引用"), CancellationToken.None);
        var frame = new MessageSendFrame(
            "idem-reply-" + Guid.NewGuid().ToString("N"),
            conversation.Id,
            MessageType.Text,
            MessageContent.FromString("幂等回复"),
            original.Message.MessageId);

        var first = await service.SendAsync(sender.Id, frame, CancellationToken.None);
        db.ChangeTracker.Clear();
        var second = await service.SendAsync(sender.Id, frame, CancellationToken.None);

        Assert.True(first.Created);
        Assert.False(second.Created);
        Assert.Equal(first.Message.MessageId, second.Message.MessageId);
        Assert.NotNull(first.Message.Reply);
        Assert.NotNull(second.Message.Reply);
        Assert.Equal(original.Message.MessageId, second.Message.Reply!.MessageId);
    }

    [Fact]
    public async Task SendAsync_SystemReply_UsesSystemText()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-sys-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-sys-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);

        // 直接落一条系统消息（模拟建群/邀请系统消息的落库形态），last_seq 同步推进。
        var systemId = Guid.NewGuid().ToString("D");
        db.Messages.Add(new MessageEntity
        {
            Id = systemId,
            ConversationId = conversation.Id,
            SenderId = sender.Id,
            ClientMsgId = Guid.NewGuid().ToString("N"),
            Seq = 1,
            MsgType = MessageType.System,
            ContentJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["event"] = "group.created",
                ["text"] = "ALICE UI01 创建了群聊",
            }),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
        var conversationRow = await db.Conversations.SingleAsync(item => item.Id == conversation.Id);
        conversationRow.LastSeq = 1;
        conversationRow.LastMessageId = systemId;
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var service = TestSupport.CreateMessageService(db);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "收到", systemId),
            CancellationToken.None);

        var summary = reply.Message.Reply;
        Assert.NotNull(summary);
        Assert.Equal(MessageType.System, summary.MsgType);
        Assert.Equal("ALICE UI01 创建了群聊", summary.Text);
        Assert.Null(summary.FileId);
    }

    private static MessageSendFrame NewText(string conversationId, string text, string? replyTo = null) =>
        new(
            "r-" + Guid.NewGuid().ToString("N"),
            conversationId,
            MessageType.Text,
            MessageContent.FromString(text),
            replyTo);

    private static MessageSendFrame NewMedia(string conversationId, MessageType msgType, FileContent content) =>
        new(
            "r-" + Guid.NewGuid().ToString("N"),
            conversationId,
            msgType,
            MessageContent.FromObject(content));

}
