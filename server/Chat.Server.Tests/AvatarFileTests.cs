using Chat.Protocol;
using Chat.Server.BackgroundJobs;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// F-12-01：头像文件对已登录用户可见 + 孤儿清理把 users.avatar_file_id 纳入引用保护。
/// 用真实 Sqlite 临时库与真实 FileService/FileStorage，不 mock 业务逻辑。
/// </summary>
public sealed class AvatarFileTests
{
    [Fact]
    public async Task AvatarFile_IsDownloadableByOtherLoggedInUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "avatar-owner@example.com", "头像主人");
        var viewer = await TestSupport.SeedUserAsync(db, "avatar-viewer@example.com", "查看者");
        await using var storage = TempFileStorage.Create();
        var file = await SeedFileAsync(db, storage.Instance, owner.Id, image: true);
        owner.AvatarFileId = file.Id;
        await db.SaveChangesAsync();

        var service = CreateFileService(db, storage.Instance);
        var result = await service.DownloadAsync(viewer.Id, file.Id, CancellationToken.None);

        Assert.Equal(file.Id, result.File.Id);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task UnreferencedFile_IsForbiddenForOtherUser()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "avatar-owner2@example.com");
        var viewer = await TestSupport.SeedUserAsync(db, "avatar-viewer2@example.com");
        await using var storage = TempFileStorage.Create();
        var file = await SeedFileAsync(db, storage.Instance, owner.Id, image: true);

        var service = CreateFileService(db, storage.Instance);
        await TestSupport.AssertApiExceptionAsync(
            ErrorCodes.Forbidden,
            () => service.DownloadAsync(viewer.Id, file.Id, CancellationToken.None),
            403);
    }

    [Fact]
    public async Task CleanupCandidates_ExcludeAvatarAndMessageReferencedFiles()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "avatar-cleanup@example.com");
        var other = await TestSupport.SeedUserAsync(db, "avatar-cleanup2@example.com");
        await using var storage = TempFileStorage.Create();

        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (25L * 60 * 60 * 1000);
        var avatarFile = await SeedFileAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        var messageFile = await SeedFileAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        var orphanFile = await SeedFileAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);

        owner.AvatarFileId = avatarFile.Id;
        var conversation = await TestSupport.SeedDirectConversationAsync(db, owner, other);
        db.Messages.Add(new MessageEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            SenderId = owner.Id,
            ClientMsgId = Guid.NewGuid().ToString("N"),
            Seq = 1,
            MsgType = MessageType.Image,
            ContentJson = "{\"fileId\":\"" + messageFile.Id + "\"}",
            FileId = messageFile.Id,
            CreatedAt = old,
        });
        await db.SaveChangesAsync();

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (24L * 60 * 60 * 1000);
        var candidates = await OrphanFileCleanupService
            .QueryOrphanCandidates(db, cutoff, 500)
            .Select(file => file.Id)
            .ToListAsync();

        Assert.Contains(orphanFile.Id, candidates);
        Assert.DoesNotContain(avatarFile.Id, candidates);
        Assert.DoesNotContain(messageFile.Id, candidates);
    }

    [Fact]
    public async Task ReplaceAvatar_OldFileBecomesUnreferenced()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var owner = await TestSupport.SeedUserAsync(db, "avatar-replace@example.com");
        await using var storage = TempFileStorage.Create();

        var old = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (25L * 60 * 60 * 1000);
        var first = await SeedFileAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        var second = await SeedFileAsync(db, storage.Instance, owner.Id, image: true, createdAt: old);
        owner.AvatarFileId = first.Id;
        await db.SaveChangesAsync();
        owner.AvatarFileId = second.Id;
        await db.SaveChangesAsync();

        Assert.False(await OrphanFileCleanupService.IsFileReferencedAsync(db, first.Id, CancellationToken.None));
        Assert.True(await OrphanFileCleanupService.IsFileReferencedAsync(db, second.Id, CancellationToken.None));

        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - (24L * 60 * 60 * 1000);
        var candidates = await OrphanFileCleanupService
            .QueryOrphanCandidates(db, cutoff, 500)
            .Select(file => file.Id)
            .ToListAsync();
        Assert.Contains(first.Id, candidates);
        Assert.DoesNotContain(second.Id, candidates);
    }

    private static FileService CreateFileService(ChatDbContext db, FileStorage storage)
    {
        var options = TestSupport.CreateAppOptions();
        options.FileDirectory = storage.RootDirectory;
        return new FileService(db, storage, Options.Create(options), NullLogger<FileService>.Instance);
    }

    private static async Task<StoredFile> SeedFileAsync(
        ChatDbContext db,
        FileStorage storage,
        string ownerId,
        bool image,
        long? createdAt = null)
    {
        var id = Guid.NewGuid().ToString("D");
        var extension = image ? ".png" : ".pdf";
        var mime = image ? "image/png" : "application/pdf";
        var storagePath = $"202610/{id}{extension}";
        var fullPath = Path.Combine(storage.RootDirectory, storagePath)
            .Replace('\\', Path.DirectorySeparatorChar);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllBytesAsync(fullPath, new byte[] { 0x89, 0x50, 0x4E, 0x47 });

        var file = new StoredFile
        {
            Id = id,
            OwnerId = ownerId,
            OriginalName = image ? "头像.png" : "文档.pdf",
            Mime = mime,
            Size = 4,
            StoragePath = storagePath,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        db.Files.Add(file);
        await db.SaveChangesAsync();
        return file;
    }

    /// <summary>临时文件目录 + FileStorage，用完删除目录。</summary>
    private sealed class TempFileStorage : IAsyncDisposable
    {
        private TempFileStorage(string directory, FileStorage instance)
        {
            Directory = directory;
            Instance = instance;
        }

        private string Directory { get; }

        public FileStorage Instance { get; }

        public static TempFileStorage Create()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "eisechat-avatar-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(directory);
            var options = TestSupport.CreateAppOptions();
            options.FileDirectory = directory;
            var storage = new FileStorage(
                Options.Create(options),
                NullLogger<FileStorage>.Instance);
            return new TempFileStorage(directory, storage);
        }

        public ValueTask DisposeAsync()
        {
            try
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            return ValueTask.CompletedTask;
        }
    }
}
