using Chat.Protocol;
using Chat.Server.BackgroundJobs;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Files;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chat.Server.Tests;

/// <summary>
/// F-23：群头像（conversations.avatar_file_id）更新、校验、下载可见性与孤儿清理引用保护。
/// 契约见 protocol/protocol.md §4.5（Conversation.avatarFileId，单聊恒 null；PATCH 空串清空）。
/// </summary>
public sealed class ConversationAvatarTests
{
    [Fact]
    public async Task SetAvatar_ReturnsAndPersists()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner1@example.com", "甲");
        var member = await TestSupport.SeedUserAsync(db, "ca-member1@example.com", "乙");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: file.Id, CancellationToken.None);

        Assert.Equal(file.Id, updated.Conversation.AvatarFileId);
        var fetched = await service.GetAsync(member.Id, conversation.Id, CancellationToken.None);
        Assert.Equal(file.Id, fetched.AvatarFileId);
    }

    [Fact]
    public async Task ClearAvatar_SetsNull()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner2@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member2@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        conversation.AvatarFileId = file.Id;
        await db.SaveChangesAsync();
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: "", CancellationToken.None);

        Assert.Null(updated.Conversation.AvatarFileId);
        var entity = await db.Conversations.AsNoTracking().SingleAsync(item => item.Id == conversation.Id);
        Assert.Null(entity.AvatarFileId);
    }

    [Fact]
    public async Task ReplaceAvatar_UpdatesToNewFile()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner3@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member3@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var first = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        var second = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        conversation.AvatarFileId = first.Id;
        await db.SaveChangesAsync();
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: second.Id, CancellationToken.None);

        Assert.Equal(second.Id, updated.Conversation.AvatarFileId);
    }

    [Fact]
    public async Task NonOwner_Forbidden()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner4@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member4@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, member.Id, image: true);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.UpdateGroupProfileAsync(
                member.Id, conversation.Id, title: null, announcement: null, avatarFileId: file.Id, CancellationToken.None),
            StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task NonMember_NotMember()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner5@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member5@example.com");
        var outsider = await TestSupport.SeedUserAsync(db, "ca-outsider5@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, outsider.Id, image: true);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.NotMember,
            () => service.UpdateGroupProfileAsync(
                outsider.Id, conversation.Id, title: null, announcement: null, avatarFileId: file.Id, CancellationToken.None),
            StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task DirectConversation_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner6@example.com");
        var other = await TestSupport.SeedUserAsync(db, "ca-other6@example.com");
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(
                owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: file.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task EmptyRequest_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner7@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member7@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(
                owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: null, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task MissingFile_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner8@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member8@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(
                owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: "does-not-exist", CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task OtherUserFile_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner9@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member9@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var memberFile = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, member.Id, image: true);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(
                owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: memberFile.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task NonImageFile_ValidationFailed()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner10@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member10@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var pdf = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: false);
        var service = TestSupport.CreateConversationService(db);

        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.ValidationFailed,
            () => service.UpdateGroupProfileAsync(
                owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: pdf.Id, CancellationToken.None),
            StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task UpdateTitleAndAnnouncement_KeepsAvatar()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner11@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member11@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        conversation.AvatarFileId = file.Id;
        conversation.Announcement = "旧公告";
        await db.SaveChangesAsync();
        var service = TestSupport.CreateConversationService(db);

        var updated = await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: "新群名", announcement: "新公告", avatarFileId: null, CancellationToken.None);

        Assert.Equal("新群名", updated.Conversation.Title);
        Assert.Equal("新公告", updated.Conversation.Announcement);
        Assert.Equal(file.Id, updated.Conversation.AvatarFileId);
    }

    [Fact]
    public async Task GroupAvatarFile_IsDownloadableByOtherLoggedInUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner12@example.com");
        var member = await TestSupport.SeedUserAsync(db, "ca-member12@example.com");
        var viewer = await TestSupport.SeedUserAsync(db, "ca-viewer12@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, member);
        await using var storage = TempFileStorage.Create();
        var file = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true);
        conversation.AvatarFileId = file.Id;
        await db.SaveChangesAsync();

        var fileService = TestSupport.CreateFileService(db, storage.Instance);
        var result = await fileService.DownloadAsync(viewer.Id, file.Id, CancellationToken.None);

        Assert.Equal(file.Id, result.File.Id);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task GroupAvatarReference_ExcludedFromCleanup_ThenRecycledAfterClear()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "ca-owner13@example.com");
        var other = await TestSupport.SeedUserAsync(db, "ca-other13@example.com");
        var conversation = await TestSupport.SeedGroupConversationAsync(db, owner, other);
        await using var storage = TempFileStorage.Create();

        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (25L * 60 * 60 * 1000);
        var avatarFile = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        var orphanFile = await TestSupport.SeedFileOnDiskAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        conversation.AvatarFileId = avatarFile.Id;
        await db.SaveChangesAsync();

        Assert.True(await OrphanFileCleanupService.IsFileReferencedAsync(db, avatarFile.Id, CancellationToken.None));
        Assert.False(await OrphanFileCleanupService.IsFileReferencedAsync(db, orphanFile.Id, CancellationToken.None));

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (24L * 60 * 60 * 1000);
        var candidates = await OrphanFileCleanupService
            .QueryOrphanCandidates(db, cutoff, 500)
            .Select(file => file.Id)
            .ToListAsync();
        Assert.DoesNotContain(avatarFile.Id, candidates);
        Assert.Contains(orphanFile.Id, candidates);

        var service = TestSupport.CreateConversationService(db);
        await service.UpdateGroupProfileAsync(
            owner.Id, conversation.Id, title: null, announcement: null, avatarFileId: "", CancellationToken.None);

        Assert.False(await OrphanFileCleanupService.IsFileReferencedAsync(db, avatarFile.Id, CancellationToken.None));
        var afterClear = await OrphanFileCleanupService
            .QueryOrphanCandidates(db, cutoff, 500)
            .Select(file => file.Id)
            .ToListAsync();
        Assert.Contains(avatarFile.Id, afterClear);
    }



}
