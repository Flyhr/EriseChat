using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Files;
using Chat.Server.Modules.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chat.Server.Tests;

/// <summary>
/// F-28 单测：语音消息（protocol/protocol.md §7 第 9 条）。
/// 覆盖 content 归一化、fileId 归属、mime/大小/时长校验、撤回与共享文件汇总排除。
/// </summary>
public sealed class MessageVoiceTests
{
    [Fact]
    public async Task SendVoice_Valid_PersistsNormalizedContentAndFileId()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-a@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-b@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "voice.webm", "audio/webm", 32 * 1024);
        var service = TestSupport.CreateMessageService(db);

        var result = await service.SendAsync(
            sender.Id,
            NewVoice(conversation.Id, file.Id, 1200),
            CancellationToken.None);

        Assert.Equal(MessageType.Voice, result.Message.MsgType);
        var content = result.Message.Content.As<VoiceContent>();
        Assert.NotNull(content);
        Assert.Equal(file.Id, content.FileId);
        Assert.Equal(1200, content.DurationMs);

        var row = await db.Messages.AsNoTracking().SingleAsync(message => message.Id == result.Message.MessageId);
        Assert.Equal(file.Id, row.FileId);
        Assert.Equal(MessageType.Voice, row.MsgType);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(60001)]
    public async Task SendVoice_DurationOutOfRange_IsValidationFailed(long durationMs)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-b@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-c@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "voice.webm", "audio/webm");
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewVoice(conversation.Id, file.Id, durationMs),
                CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SendVoice_FileOwnedByOtherUser_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-d@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-e@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var foreignFile = await TestSupport.SeedFileAsync(db, other.Id, "voice.webm", "audio/webm");
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewVoice(conversation.Id, foreignFile.Id, 1500),
                CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SendVoice_NonAudioMime_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-f@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-g@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "note.txt", "text/plain");
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(
                sender.Id,
                NewVoice(conversation.Id, file.Id, 1500),
                CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SendVoice_FileTooLarge_IsMessageTooLarge()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-h@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-i@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(
            db,
            sender.Id,
            "voice.webm",
            "audio/webm",
            MessageService.VoiceMaxBytes + 1);
        var service = TestSupport.CreateMessageService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.MessageTooLarge,
            () => service.SendAsync(
                sender.Id,
                NewVoice(conversation.Id, file.Id, 1500),
                CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task SendVoice_StringContent_IsValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-j@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-k@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var service = TestSupport.CreateMessageService(db);

        var frame = new MessageSendFrame(
            "r-" + Guid.NewGuid().ToString("N"),
            conversation.Id,
            MessageType.Voice,
            MessageContent.FromString("oops"),
            null);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.SendAsync(sender.Id, frame, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Recall_VoiceMessage_ClearsContentAndKeepsFileId()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-l@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-m@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var file = await TestSupport.SeedFileAsync(db, sender.Id, "voice.webm", "audio/webm");
        var service = TestSupport.CreateMessageService(db);

        var sent = await service.SendAsync(
            sender.Id,
            NewVoice(conversation.Id, file.Id, 2500),
            CancellationToken.None);
        var recalled = await service.RecallAsync(
            sender.Id,
            conversation.Id,
            sent.Message.MessageId,
            CancellationToken.None);

        Assert.True(recalled.Changed);
        Assert.NotNull(recalled.Message.DeletedAt);
        Assert.Equal(string.Empty, recalled.Message.Content.GetString());

        var row = await db.Messages.AsNoTracking().SingleAsync(message => message.Id == sent.Message.MessageId);
        Assert.NotNull(row.DeletedAt);
        Assert.Equal(file.Id, row.FileId);
    }

    [Fact]
    public async Task SharedFiles_ExcludeVoiceMessages()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var sender = await TestSupport.SeedUserAsync(db, "voice-n@example.com");
        var other = await TestSupport.SeedUserAsync(db, "voice-o@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, sender, other);
        var voiceFile = await TestSupport.SeedFileAsync(db, sender.Id, "voice.webm", "audio/webm");
        var imageFile = await TestSupport.SeedFileAsync(db, sender.Id, "photo.png", "image/png", 2048);
        var service = TestSupport.CreateMessageService(db);

        await service.SendAsync(
            sender.Id,
            NewVoice(conversation.Id, voiceFile.Id, 3000),
            CancellationToken.None);
        await service.SendAsync(
            sender.Id,
            new MessageSendFrame(
                "r-" + Guid.NewGuid().ToString("N"),
                conversation.Id,
                MessageType.Image,
                MessageContent.FromObject(new FileContent(imageFile.Id, "photo.png", 2048, "image/png")),
                null),
            CancellationToken.None);

        var filesService = new ConversationFilesService(db, NullLogger<ConversationFilesService>.Instance);
        var images = await filesService.GetSharedFilesAsync(
            sender.Id,
            conversation.Id,
            "image",
            null,
            null,
            CancellationToken.None);
        var single = Assert.Single(images.Items);
        Assert.Equal(imageFile.Id, single.FileId);

        var documents = await filesService.GetSharedFilesAsync(
            sender.Id,
            conversation.Id,
            "file",
            null,
            null,
            CancellationToken.None);
        Assert.Empty(documents.Items);
    }

    private static MessageSendFrame NewVoice(string conversationId, string fileId, long durationMs) =>
        new(
            "r-" + Guid.NewGuid().ToString("N"),
            conversationId,
            MessageType.Voice,
            MessageContent.FromObject(new VoiceContent(fileId, durationMs)),
            null);
}
