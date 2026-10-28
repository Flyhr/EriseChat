using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// F-15：会话共享文件汇总（ConversationFilesService）单测。
/// 只统计 messages.file_id 非空的 image/file 消息，按文件去重取最新引用 seq，seq 降序分页。
/// </summary>
public sealed class ConversationFilesTests
{
    private const string ImageMime = "image/png";
    private const string TextMime = "text/plain";

    [Fact]
    public async Task OnlyImageAndFileMessagesAreReturned()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var imageFile = await SeedFileAsync(db, owner.Id, "a.png", ImageMime);
        var textFile = await SeedFileAsync(db, owner.Id, "a.txt", TextMime);

        await SeedMessageAsync(db, conversation, owner, 1, MessageType.Text, "\"hello\"");
        await SeedMessageAsync(db, conversation, owner, 2, MessageType.Image, "\"\"", imageFile.Id);
        await SeedMessageAsync(db, conversation, owner, 3, MessageType.File, "\"\"", textFile.Id);

        var service = CreateService(db);
        var images = await service.GetSharedFilesAsync(owner.Id, conversation.Id, "image", null, null, CancellationToken.None);
        var files = await service.GetSharedFilesAsync(owner.Id, conversation.Id, "file", null, null, CancellationToken.None);

        Assert.Single(images.Items);
        Assert.Equal(imageFile.Id, images.Items[0].FileId);
        Assert.Single(files.Items);
        Assert.Equal(textFile.Id, files.Items[0].FileId);
        Assert.DoesNotContain(images.Items, item => item.FileId == textFile.Id);
    }

    [Fact]
    public async Task UnsentUploadedFileIsExcluded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var sentFile = await SeedFileAsync(db, owner.Id, "sent.png", ImageMime);
        await SeedFileAsync(db, owner.Id, "unsent.png", ImageMime);
        await SeedMessageAsync(db, conversation, owner, 1, MessageType.Image, "\"\"", sentFile.Id);

        var page = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(sentFile.Id, page.Items[0].FileId);
    }

    [Fact]
    public async Task DuplicateFileIdIsReturnedOnceWithLatestSeq()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var file = await SeedFileAsync(db, owner.Id, "dup.png", ImageMime);
        await SeedMessageAsync(db, conversation, owner, 2, MessageType.Image, "\"\"", file.Id);
        var latest = await SeedMessageAsync(db, conversation, owner, 5, MessageType.Image, "\"\"", file.Id);

        var page = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None);

        var item = Assert.Single(page.Items);
        Assert.Equal(file.Id, item.FileId);
        Assert.Equal(latest.Seq, item.MessageSeq);
        Assert.Equal(latest.Id, item.MessageId);
    }

    [Fact]
    public async Task ItemsAreOrderedByMessageSeqDescending()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var first = await SeedFileAsync(db, owner.Id, "first.png", ImageMime);
        var second = await SeedFileAsync(db, owner.Id, "second.png", ImageMime);
        await SeedMessageAsync(db, conversation, owner, 2, MessageType.Image, "\"\"", first.Id);
        await SeedMessageAsync(db, conversation, owner, 4, MessageType.Image, "\"\"", second.Id);

        var page = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None);

        Assert.Collection(
            page.Items,
            item => Assert.Equal(second.Id, item.FileId),
            item => Assert.Equal(first.Id, item.FileId));
    }

    [Fact]
    public async Task BeforeSeqPaginationHasNoDuplicateOrMissing()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var files = new List<MsgFileSeed>();
        for (var index = 0; index < 3; index += 1)
        {
            var file = await SeedFileAsync(db, owner.Id, $"p{index}.png", ImageMime);
            var seq = (index + 1) * 2L;
            await SeedMessageAsync(db, conversation, owner, seq, MessageType.Image, "\"\"", file.Id);
            files.Add(new MsgFileSeed(file.Id, seq));
        }

        var service = CreateService(db);
        var firstPage = await service.GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, 2, CancellationToken.None);
        Assert.Equal(2, firstPage.Items.Count);
        Assert.True(firstPage.HasMore);
        Assert.Equal(6L, firstPage.Items[0].MessageSeq);
        Assert.Equal(4L, firstPage.Items[1].MessageSeq);

        var secondPage = await service.GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", firstPage.Items[1].MessageSeq, 2, CancellationToken.None);
        var item = Assert.Single(secondPage.Items);
        Assert.False(secondPage.HasMore);
        Assert.Equal(2L, item.MessageSeq);
        Assert.Equal(files[0].FileId, item.FileId);
        Assert.Equal(
            files.Select(seed => seed.FileId).OrderBy(id => id),
            firstPage.Items.Concat(secondPage.Items).Select(item => item.FileId).OrderBy(id => id));
    }

    [Fact]
    public async Task HasMoreIsFalseWhenPageExactlyFillsLimit()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        for (var index = 0; index < 2; index += 1)
        {
            var file = await SeedFileAsync(db, owner.Id, $"exact{index}.png", ImageMime);
            await SeedMessageAsync(db, conversation, owner, index + 1, MessageType.Image, "\"\"", file.Id);
        }

        var page = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, 2, CancellationToken.None);

        Assert.Equal(2, page.Items.Count);
        Assert.False(page.HasMore);
    }

    [Fact]
    public async Task NonMemberIsForbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var outsider = await TestSupport.SeedUserAsync(db, "f15-out@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var file = await SeedFileAsync(db, owner.Id, "private.png", ImageMime);
        await SeedMessageAsync(db, conversation, owner, 1, MessageType.Image, "\"\"", file.Id);

        var ownerPage = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None);
        Assert.Single(ownerPage.Items);

        var outsiderError = await Assert.ThrowsAsync<ApiException>(() =>
            CreateService(db).GetSharedFilesAsync(outsider.Id, conversation.Id, "image", null, null, CancellationToken.None));
        Assert.Equal(ErrorCodes.NotMember, outsiderError.Code);
    }

    [Fact]
    public async Task ConversationNotFound()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            CreateService(db).GetSharedFilesAsync(
                owner.Id,
                Guid.NewGuid().ToString("D"),
                "image",
                null,
                null,
                CancellationToken.None));

        Assert.Equal(ErrorCodes.NotFound, error.Code);
    }

    [Theory]
    [InlineData("video")]
    [InlineData("text")]
    [InlineData("")]
    public async Task InvalidTypeIsRejected(string type)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            CreateService(db).GetSharedFilesAsync(owner.Id, conversation.Id, type, null, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(101)]
    [InlineData(-1)]
    public async Task LimitOutOfRangeIsRejected(int limit)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            CreateService(db).GetSharedFilesAsync(owner.Id, conversation.Id, "image", null, limit, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
    }

    [Fact]
    public async Task InvalidBeforeSeqIsRejected()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);

        var error = await Assert.ThrowsAsync<ApiException>(() =>
            CreateService(db).GetSharedFilesAsync(owner.Id, conversation.Id, "image", 0, null, CancellationToken.None));

        Assert.Equal(ErrorCodes.ValidationFailed, error.Code);
    }

    [Fact]
    public async Task FieldsMatchFilesAndMessagesRecords()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner);
        var file = await SeedFileAsync(db, owner.Id, "设计稿.png", ImageMime, size: 2048, createdAt: 1_800_000_000_000);
        var message = await SeedMessageAsync(
            db,
            conversation,
            owner,
            7,
            MessageType.Image,
            "\"\"",
            file.Id,
            createdAt: 1_800_000_001_000);

        var item = Assert.Single((await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None)).Items);

        Assert.Equal(file.Id, item.FileId);
        Assert.Equal(file.OriginalName, item.Name);
        Assert.Equal(file.Size, item.Size);
        Assert.Equal(file.Mime, item.Mime);
        Assert.Equal(message.Id, item.MessageId);
        Assert.Equal(7, item.MessageSeq);
        Assert.Equal(owner.Id, item.SenderId);
        Assert.Equal(message.CreatedAt, item.CreatedAt);
    }

    [Fact]
    public async Task AvatarFilesAndOtherConversationsAreExcluded()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = new ChatDbContext(database.Options);
        var owner = await TestSupport.SeedUserAsync(db, "f15-owner@example.com");
        var other = await TestSupport.SeedUserAsync(db, "f15-other@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, other);
        var otherConversation = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        var mine = await SeedFileAsync(db, owner.Id, "mine.png", ImageMime);
        var foreign = await SeedFileAsync(db, other.Id, "other.png", ImageMime);
        await SeedMessageAsync(db, conversation, owner, 1, MessageType.Image, "\"\"", mine.Id);
        await SeedMessageAsync(db, otherConversation, other, 1, MessageType.Image, "\"\"", foreign.Id);

        owner.AvatarFileId = foreign.Id;
        await db.SaveChangesAsync();

        var page = await CreateService(db).GetSharedFilesAsync(
            owner.Id, conversation.Id, "image", null, null, CancellationToken.None);

        Assert.Single(page.Items);
        Assert.Equal(mine.Id, page.Items[0].FileId);
    }

    private sealed record MsgFileSeed(string FileId, long Seq);

    private static ConversationFilesService CreateService(ChatDbContext db) =>
        new(db, NullLogger<ConversationFilesService>.Instance);

    private static async Task<StoredFile> SeedFileAsync(
        ChatDbContext db,
        string ownerId,
        string name,
        string mime,
        long size = 128,
        long? createdAt = null)
    {
        var file = new StoredFile
        {
            Id = Guid.NewGuid().ToString("D"),
            OwnerId = ownerId,
            OriginalName = name,
            Mime = mime,
            Size = size,
            StoragePath = $"202610/{Guid.NewGuid():N}",
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    private static async Task<MessageEntity> SeedMessageAsync(
        ChatDbContext db,
        ConversationEntity conversation,
        User sender,
        long seq,
        MessageType msgType,
        string contentJson,
        string? fileId = null,
        long? createdAt = null)
    {
        var message = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            SenderId = sender.Id,
            ClientMsgId = Guid.NewGuid().ToString("D"),
            Seq = seq,
            MsgType = msgType,
            ContentJson = contentJson,
            FileId = fileId,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.Messages.Add(message);
        conversation.LastSeq = Math.Max(conversation.LastSeq, seq);
        await db.SaveChangesAsync();
        return message;
    }
}
