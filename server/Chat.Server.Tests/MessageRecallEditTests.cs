using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Messages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// F-18 单测：消息撤回与编辑（protocol/protocol.md §4.3）。
/// 覆盖发送者权限、窗口、幂等、撤回后置空内容、编辑校验与历史/sync 一致性、reply 摘要「[已撤回]」。
/// 时间窗口相关用例使用独立 AppOptions（窗口 1 秒）或回拨 created_at，避免等待。
/// </summary>
public sealed class MessageRecallEditTests
{
    [Fact]
    public async Task Recall_BySender_SetsDeletedAtAndEmptyContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "待撤回"), CancellationToken.None);
        var recalled = await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);

        Assert.True(recalled.Changed);
        Assert.NotNull(recalled.Message.DeletedAt);
        Assert.True(recalled.Message.Content.IsString);
        Assert.Equal(string.Empty, recalled.Message.Content.GetString());
        Assert.Equal(2, recalled.MemberUserIds.Count);

        var entity = await db.Messages.AsNoTracking().FirstAsync(item => item.Id == sent.Message.MessageId);
        Assert.NotNull(entity.DeletedAt);
        Assert.Equal("\"\"", entity.ContentJson);
    }

    [Fact]
    public async Task Recall_Image_KeepsFileIdButClearsVisibleContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-image-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-image-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await SeedStoredFileAsync(db, sender.Id, "图片.png", "image/png", 2048);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            sender.Id,
            NewMedia(conversation.Id, MessageType.Image, new FileContent(file.Id, file.OriginalName, file.Size, file.Mime)),
            CancellationToken.None);
        var recalled = await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);

        Assert.Equal(string.Empty, recalled.Message.Content.GetString());
        var entity = await db.Messages.AsNoTracking().FirstAsync(item => item.Id == sent.Message.MessageId);
        // file_id 保留供孤儿清理引用（协议 §4.3 规则 4），但 GET 返回的 content 不再带文件信息。
        Assert.Equal(file.Id, entity.FileId);
        Assert.Equal("\"\"", entity.ContentJson);
    }

    [Fact]
    public async Task Recall_ByOtherUser_IsForbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-f-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-f-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "不是你的消息"), CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.RecallAsync(other.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task Recall_SystemMessage_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-sys-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-sys-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var system = await SeedMessageAsync(
            db,
            conversation,
            sender.Id,
            MessageType.System,
            "{\"event\":\"group.created\",\"text\":\"创建了群聊\"}");
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.RecallAsync(sender.Id, conversation.Id, system.Id, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Recall_OutsideWindow_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-window-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-window-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "很久以前"), CancellationToken.None);
        await ShiftCreatedAtAsync(db, sent.Message.MessageId, secondsAgo: 200);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Recall_SecondTime_IsIdempotentAndKeepsDeletedAt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-idem-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-idem-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "重复撤回"), CancellationToken.None);
        var first = await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);
        var second = await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);

        Assert.True(first.Changed);
        Assert.False(second.Changed);
        Assert.Equal(first.Message.DeletedAt, second.Message.DeletedAt);
    }

    [Fact]
    public async Task Recall_NotMember_IsNotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-nm-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-nm-b@example.com");
        var outsider = await TestSupport.SeedUserAsync(db, "recall-nm-c@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "外人不可见"), CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.RecallAsync(outsider.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task Recall_MessageNotFound_IsNotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-404-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-404-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotFound,
            () => service.RecallAsync(
                sender.Id,
                conversation.Id,
                Guid.NewGuid().ToString("D"),
                CancellationToken.None),
            404);
    }

    [Fact]
    public async Task History_AfterRecall_ReturnsDeletedAtAndEmptyContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "recall-history-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "recall-history-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "历史里应显示已撤回"), CancellationToken.None);
        await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);

        var page = await service.GetHistoryAsync(sender.Id, conversation.Id, null, 50, CancellationToken.None);
        var recalled = Assert.Single(page.Messages);
        Assert.NotNull(recalled.DeletedAt);
        Assert.Equal(string.Empty, recalled.Content.GetString());
    }

    [Fact]
    public async Task Edit_BySender_UpdatesContentAndEditedAt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "原始文本"), CancellationToken.None);
        var edited = await service.EditAsync(
            sender.Id,
            conversation.Id,
            sent.Message.MessageId,
            "  修改后的文本  ",
            CancellationToken.None);

        Assert.Equal("修改后的文本", edited.Message.Content.GetString());
        Assert.NotNull(edited.Message.EditedAt);
        Assert.Equal(2, edited.MemberUserIds.Count);

        var entity = await db.Messages.AsNoTracking().FirstAsync(item => item.Id == sent.Message.MessageId);
        Assert.NotNull(entity.EditedAt);
        // content_json 由 FrameCodec 序列化（非 ASCII 会转义为 \uXXXX），按 JSON 解析后比较原文。
        using var document = JsonDocument.Parse(entity.ContentJson);
        Assert.Equal("修改后的文本", document.RootElement.GetString());
    }

    [Fact]
    public async Task Edit_ByOtherUser_IsForbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-f-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-f-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "只能自己编辑"), CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.EditAsync(other.Id, conversation.Id, sent.Message.MessageId, "改你的", CancellationToken.None),
            403);
    }

    [Fact]
    public async Task Edit_ImageMessage_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-img-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-img-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await SeedStoredFileAsync(db, sender.Id, "图.png", "image/png", 1024);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            sender.Id,
            NewMedia(conversation.Id, MessageType.Image, new FileContent(file.Id, file.OriginalName, file.Size, file.Mime)),
            CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "改成文字", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Edit_RecalledMessage_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-recalled-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-recalled-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "先撤回"), CancellationToken.None);
        await service.RecallAsync(sender.Id, conversation.Id, sent.Message.MessageId, CancellationToken.None);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "再编辑", CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Edit_OutsideWindow_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-window-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-window-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "很久以前"), CancellationToken.None);
        await ShiftCreatedAtAsync(db, sent.Message.MessageId, secondsAgo: 1000);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "超时编辑", CancellationToken.None),
            400);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("超长")]
    public async Task Edit_InvalidContent_IsValidationFailed(string kind)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, $"edit-content-{Guid.NewGuid():N}@example.com");
        var other = await TestSupport.SeedUserAsync(db, $"edit-content-b-{Guid.NewGuid():N}@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "原文"), CancellationToken.None);
        var content = kind == "超长" ? new string('文', MessageService.TextMaxLength + 1) : kind;

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, content, CancellationToken.None),
            400);
    }

    [Fact]
    public async Task Edit_ContentAtMaxLength_IsAccepted()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-max-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-max-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "原文"), CancellationToken.None);
        var content = new string('文', MessageService.TextMaxLength);
        var edited = await service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, content, CancellationToken.None);

        Assert.Equal(MessageService.TextMaxLength, edited.Message.Content.GetString()!.Length);
    }

    [Fact]
    public async Task Edit_MultipleTimes_RefreshesEditedAt()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "edit-multi-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "edit-multi-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "原文"), CancellationToken.None);
        var first = await service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "第一次", CancellationToken.None);
        await Task.Delay(5);
        var second = await service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "第二次", CancellationToken.None);

        Assert.Equal("第二次", second.Message.Content.GetString());
        Assert.True(second.Message.EditedAt >= first.Message.EditedAt);
    }

    [Fact]
    public async Task ReplyToRecalledMessage_HasRecalledTextInSummary()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "reply-recalled-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "reply-recalled-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var original = await service.SendAsync(sender.Id, NewText(conversation.Id, "会被撤回"), CancellationToken.None);
        var reply = await service.SendAsync(
            sender.Id,
            NewText(conversation.Id, "回复内容", original.Message.MessageId),
            CancellationToken.None);
        await service.RecallAsync(sender.Id, conversation.Id, original.Message.MessageId, CancellationToken.None);

        // 撤回后新发送的回复：摘要固定「[已撤回]」，messageId 保留。
        var laterReply = await service.SendAsync(
            other.Id,
            NewText(conversation.Id, "再回复一次", original.Message.MessageId),
            CancellationToken.None);
        Assert.NotNull(laterReply.Message.Reply);
        Assert.Equal(original.Message.MessageId, laterReply.Message.Reply!.MessageId);
        Assert.Equal("[已撤回]", laterReply.Message.Reply.Text);
        Assert.Null(laterReply.Message.Reply.FileId);

        // 历史重新加载已有回复时，摘要同样应按最新状态显示「[已撤回]」。
        var page = await service.GetHistoryAsync(other.Id, conversation.Id, null, 50, CancellationToken.None);
        var existingReply = page.Messages.First(item => item.MessageId == reply.Message.MessageId);
        Assert.NotNull(existingReply.Reply);
        Assert.Equal("[已撤回]", existingReply.Reply!.Text);
    }

    [Fact]
    public async Task Sync_AfterEdit_ReturnsLatestContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "sync-edit-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "sync-edit-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(sender.Id, NewText(conversation.Id, "原文"), CancellationToken.None);
        await service.EditAsync(sender.Id, conversation.Id, sent.Message.MessageId, "编辑后文本", CancellationToken.None);

        var result = await service.SyncAsync(
            other.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 0) }),
            CancellationToken.None);

        var message = Assert.Single(Assert.Single(result.Batches).Messages);
        Assert.Equal("编辑后文本", message.Content.GetString());
        Assert.NotNull(message.EditedAt);
    }

    private static MessageService CreateService(ChatDbContext db, int recallWindowSeconds = 120, int editWindowSeconds = 900) =>
        new(
            db,
            Options.Create(new AppOptions
            {
                DataDirectory = ".",
                FileDirectory = ".",
                UploadMaxBytes = 50L * 1024 * 1024,
                ProtocolVersion = 1,
                MessageRecallWindowSeconds = recallWindowSeconds,
                MessageEditWindowSeconds = editWindowSeconds,
            }));

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

    private static async Task<MessageEntity> SeedMessageAsync(
        ChatDbContext db,
        ConversationEntity conversation,
        string senderId,
        MessageType msgType,
        string contentJson)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            SenderId = senderId,
            ClientMsgId = Guid.NewGuid().ToString("N"),
            Seq = conversation.LastSeq + 1,
            MsgType = msgType,
            ContentJson = contentJson,
            CreatedAt = now,
        };
        db.Messages.Add(entity);
        conversation.LastSeq = entity.Seq;
        conversation.LastMessageId = entity.Id;
        await db.SaveChangesAsync();
        return entity;
    }

    private static async Task ShiftCreatedAtAsync(ChatDbContext db, string messageId, int secondsAgo)
    {
        var entity = await db.Messages.FirstAsync(item => item.Id == messageId);
        entity.CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-secondsAgo).ToUnixTimeMilliseconds();
        await db.SaveChangesAsync();
    }

    private static async Task<StoredFile> SeedStoredFileAsync(
        ChatDbContext db,
        string ownerId,
        string name,
        string mime,
        long size)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            OriginalName = name,
            Mime = mime,
            Size = size,
            StoragePath = Path.Combine("test", Guid.NewGuid().ToString("N") + ".bin"),
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file;
    }
}
