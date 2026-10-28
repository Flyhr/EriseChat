using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：图片/文件消息单测（M3-03 行为）。覆盖 content 以 files 记录为准、messages.file_id 落库、
/// 文件归属校验与 seq 不前进、text file_id 为 NULL、历史/sync 与落库一致。
/// </summary>
public sealed class FileMessageTests
{
    [Fact]
    public async Task SendAsync_ImageContent_IsNormalizedFromFilesRowAndPersistsFileId()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "fm-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "fm-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "照片.png", "image/png", 2048);
        db.ChangeTracker.Clear();
        var service = TestSupport.CreateMessageService(db);

        // 客户端故意传错的 name/size/mime：服务端必须用 files 记录归一。
        var clientContent = new FileContent(file.Id, "伪名.exe", 999L, "text/plain");
        var result = await service.SendAsync(
            sender.Id,
            NewFile(conversation.Id, MessageType.Image, clientContent),
            CancellationToken.None);

        Assert.True(result.Created);
        var normalized = result.Message.Content.As<FileContent>();
        Assert.NotNull(normalized);
        Assert.Equal(file.Id, normalized.FileId);
        Assert.Equal(file.OriginalName, normalized.Name);
        Assert.Equal(file.Size, normalized.Size);
        Assert.Equal(file.Mime, normalized.Mime);

        var stored = await db.Messages.AsNoTracking()
            .SingleAsync(message => message.ConversationId == conversation.Id);
        Assert.Equal(file.Id, stored.FileId);
        Assert.Equal(MessageType.Image, stored.MsgType);
        Assert.Equal(result.Message.MessageId, stored.Id);
    }

    [Fact]
    public async Task SendAsync_RejectsMissingOrForeignFileWithoutAdvancingSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "fm2-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "fm2-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var foreignFile = await TestSupport.SeedFileAsync(db, other.Id, "别人的.png", "image/png", 100);
        db.ChangeTracker.Clear();
        var service = TestSupport.CreateMessageService(db);

        // 不存在的 fileId。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewFile(conversation.Id, MessageType.Image, new FileContent(Guid.NewGuid().ToString("D"), "x.png", 100, "image/png")),
                CancellationToken.None),
            400);

        // 他人的 fileId。
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewFile(conversation.Id, MessageType.File, new FileContent(foreignFile.Id, "x.png", 100, "image/png")),
                CancellationToken.None),
            400);

        // 失败不落库、不推进 last_seq。
        var storedConversation = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Equal(0, storedConversation.LastSeq);
        Assert.Equal(0, await db.Messages.AsNoTracking().CountAsync(message => message.ConversationId == conversation.Id));
    }

    [Fact]
    public async Task TextMessage_FileIdIsNull_AndHistoryAndSyncMatchStoredContent()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "fm3-sender@example.com");
        var other = await TestSupport.SeedUserAsync(db, "fm3-other@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "文档.pdf", "application/pdf", 4096);
        db.ChangeTracker.Clear();
        var service = TestSupport.CreateMessageService(db);

        var textResult = await service.SendAsync(
            sender.Id, NewText(conversation.Id, "纯文本"), CancellationToken.None);
        var imageResult = await service.SendAsync(
            sender.Id,
            NewFile(conversation.Id, MessageType.File, new FileContent(file.Id, "客户端名", 1, "application/octet-stream")),
            CancellationToken.None);

        // text 消息 file_id 为 NULL。
        var textRow = await db.Messages.AsNoTracking()
            .SingleAsync(message => message.Id == textResult.Message.MessageId);
        Assert.Null(textRow.FileId);

        // REST 历史：image/file content 与落库一致（含 fileId）。
        var history = await service.GetHistoryAsync(sender.Id, conversation.Id, beforeSeq: null, limit: 50, CancellationToken.None);
        Assert.Equal(2, history.Messages.Count);
        var historyFile = history.Messages.Single(message => message.MsgType == MessageType.File);
        Assert.Equal(imageResult.Message.MessageId, historyFile.MessageId);
        Assert.Equal(file.Id, historyFile.Content.As<FileContent>()!.FileId);

        // sync 补拉：同样一致且无重复。
        var sync = await service.SyncAsync(
            sender.Id,
            new SyncRequestFrame(new[] { new SyncCursor(conversation.Id, 0) }),
            CancellationToken.None);
        var batch = Assert.Single(sync.Batches);
        Assert.Equal(2, batch.Messages.Count);
        var syncFile = batch.Messages.Single(message => message.MsgType == MessageType.File);
        Assert.Equal(imageResult.Message.MessageId, syncFile.MessageId);
        Assert.Equal(file.Id, syncFile.Content.As<FileContent>()!.FileId);
    }


    private static MessageSendFrame NewFile(string conversationId, MessageType msgType, FileContent content) =>
        new(
            "c-" + Guid.NewGuid().ToString("N"),
            conversationId,
            msgType,
            MessageContent.FromObject(content));

    private static MessageSendFrame NewText(string conversationId, string text) =>
        new(
            "c-" + Guid.NewGuid().ToString("N"),
            conversationId,
            MessageType.Text,
            MessageContent.FromString(text));
}
