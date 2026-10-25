using System.Text;
using Chat.Protocol;
using Chat.Server.Common.Options;
using Chat.Server.Modules.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Chat.Server.Tests;

/// <summary>
/// M3-06：文件存储/上传单测。覆盖 FileStorage 路径穿越防护与读写往返、FileService 上传的
/// 白名单/魔数/大小校验与失败不落库不落盘。全部使用系统临时目录，结束后删除。
/// </summary>
public sealed class FileStorageTests
{
    /// <summary>1x1 PNG（合法魔数），用于上传成功用例。</summary>
    private static readonly byte[] OnePixelPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");

    [Fact]
    public void ResolveFullPath_RejectsTraversalAbsoluteAndEmpty()
    {
        var root = CreateTempRoot();
        try
        {
            var storage = CreateStorage(root);

            // 相对穿越（../、..\、混合）→ 解析后不在根目录内。
            Assert.Throws<InvalidOperationException>(() => storage.ResolveFullPath("../evil.png"));
            Assert.Throws<InvalidOperationException>(() => storage.ResolveFullPath("..\\evil.png"));
            Assert.Throws<InvalidOperationException>(() => storage.ResolveFullPath("202610/../../evil.png"));

            // 绝对路径 → 必须是相对根目录的路径。
            Assert.Throws<InvalidOperationException>(() => storage.ResolveFullPath(Path.Combine(root, "x.png")));

            // 空值 → ArgumentException。
            Assert.ThrowsAny<ArgumentException>(() => storage.ResolveFullPath(string.Empty));
            Assert.ThrowsAny<ArgumentException>(() => storage.ResolveFullPath("   "));

            // 正常相对路径在根目录内。
            var resolved = storage.ResolveFullPath("202610/abc.png");
            Assert.StartsWith(storage.RootDirectory, resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task SaveOpenGetLengthDelete_RoundTrips()
    {
        var root = CreateTempRoot();
        try
        {
            var storage = CreateStorage(root);
            var payload = new byte[] { 1, 2, 3, 4, 5 };
            await using var content = new MemoryStream(payload);

            var (fileId, storagePath) = await storage.SaveAsync(content, ".png", CancellationToken.None);

            Assert.False(string.IsNullOrWhiteSpace(fileId));
            Assert.StartsWith("20", storagePath); // yyyyMM/...
            Assert.EndsWith(".png", storagePath);
            Assert.Equal(payload.Length, storage.GetLength(storagePath));

            using (var stream = storage.OpenRead(storagePath))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(payload, copy.ToArray());
            }

            storage.Delete(storagePath);
            Assert.False(File.Exists(storage.ResolveFullPath(storagePath)));
            storage.Delete(storagePath); // 幂等，不抛异常
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_ValidPng_WritesDiskAndDbRow()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "upload-ok@example.com");
        var root = CreateTempRoot();
        try
        {
            var options = new AppOptions { FileDirectory = root, UploadMaxBytes = 1024 };
            var storage = new FileStorage(Options.Create(options), NullLogger<FileStorage>.Instance);
            var service = new FileService(db, storage, Options.Create(options), NullLogger<FileService>.Instance);

            var upload = await service.UploadAsync(
                user.Id,
                " 照片.PNG ",
                new MemoryStream(OnePixelPng),
                OnePixelPng.Length,
                CancellationToken.None);

            Assert.Equal(OnePixelPng.Length, upload.Size);
            Assert.Equal("image/png", upload.Mime);
            Assert.Equal("照片.PNG", upload.Name);

            var row = await db.Files.AsNoTracking().SingleAsync(file => file.Id == upload.FileId);
            Assert.Equal(user.Id, row.OwnerId);
            Assert.Equal(upload.Size, row.Size);
            Assert.Equal("image/png", row.Mime);
            Assert.Equal(upload.Name, row.OriginalName);

            // 磁盘文件存在且内容一致。
            using (var stream = storage.OpenRead(row.StoragePath))
            {
                using var copy = new MemoryStream();
                await stream.CopyToAsync(copy);
                Assert.Equal(OnePixelPng, copy.ToArray());
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UploadAsync_RejectsDisallowedTypeBadMagicTooLargeAndEmpty()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.CreateContext();
        var user = await TestSupport.SeedUserAsync(db, "upload-bad@example.com");
        var root = CreateTempRoot();
        try
        {
            var options = new AppOptions { FileDirectory = root, UploadMaxBytes = 1024 };
            var storage = new FileStorage(Options.Create(options), NullLogger<FileStorage>.Instance);
            var service = new FileService(db, storage, Options.Create(options), NullLogger<FileService>.Instance);

            // 白名单外扩展名。
            await TestSupport.AssertApiExceptionAsync(
                ErrorCodes.FileTypeNotAllowed,
                () => service.UploadAsync(user.Id, "evil.exe", new MemoryStream(OnePixelPng), OnePixelPng.Length, CancellationToken.None),
                400);

            // 扩展名合法但魔数不符。
            var text = Encoding.UTF8.GetBytes("not a png");
            await TestSupport.AssertApiExceptionAsync(
                ErrorCodes.FileTypeNotAllowed,
                () => service.UploadAsync(user.Id, "fake.png", new MemoryStream(text), text.Length, CancellationToken.None),
                400);

            // 声明大小超过上传上限。
            await TestSupport.AssertApiExceptionAsync(
                ErrorCodes.FileTooLarge,
                () => service.UploadAsync(user.Id, "big.png", new MemoryStream(OnePixelPng), 2048, CancellationToken.None),
                413);

            // 空文件。
            await TestSupport.AssertApiExceptionAsync(
                ErrorCodes.ValidationFailed,
                () => service.UploadAsync(user.Id, "empty.png", new MemoryStream(), 0, CancellationToken.None),
                400);

            // 失败用例：不写 DB 行、不留磁盘文件。
            Assert.Equal(0, await db.Files.AsNoTracking().CountAsync());
            Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "eisechat-tests", "storage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static FileStorage CreateStorage(string root) =>
        new(Options.Create(new AppOptions { FileDirectory = root }), NullLogger<FileStorage>.Instance);
}
