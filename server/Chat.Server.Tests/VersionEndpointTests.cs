using System.Security.Cryptography;
using Chat.Server.Modules.Release;
using Microsoft.Extensions.Logging.Abstractions;

namespace Chat.Server.Tests;

/// <summary>
/// 客户端更新清单（F-29，protocol/protocol.md §7 第 10 条）：
/// 无产物、单产物、多版本取最新、非法文件名忽略、sha256 缓存与失效、清单版本取最大值。
/// </summary>
public sealed class VersionEndpointTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "erisechat-tests",
        "release-" + Guid.NewGuid().ToString("N"));

    public VersionEndpointTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // 临时目录清理失败不影响测试结论。
        }
    }

    [Fact]
    public async Task GetManifest_NoArtifacts_ReturnsServerVersionAndNullArtifacts()
    {
        var manifest = await CreateService().GetManifestAsync(protocolVersion: 7);

        Assert.Equal(ReleaseArtifactService.ServerVersion(), manifest.Version);
        Assert.Equal(7, manifest.ProtocolVersion);
        Assert.Null(manifest.Artifacts.Desktop);
        Assert.Null(manifest.Artifacts.Android);
    }

    [Fact]
    public async Task GetManifest_WithArtifacts_ReturnsMetadataAndSha256()
    {
        var desktopBytes = new byte[] { 1, 2, 3, 4, 5 };
        var androidBytes = new byte[] { 9, 8, 7 };
        WriteFile("EriseChat-Setup-1.2.3.exe", desktopBytes);
        WriteFile("EriseChat-1.2.3.apk", androidBytes);

        var manifest = await CreateService().GetManifestAsync(protocolVersion: 1);

        Assert.Equal("1.2.3", manifest.Version);

        var desktop = Assert.IsType<ReleaseArtifact>(manifest.Artifacts.Desktop);
        Assert.Equal("1.2.3", desktop.Version);
        Assert.Equal("EriseChat-Setup-1.2.3.exe", desktop.FileName);
        Assert.Equal("/download/files/EriseChat-Setup-1.2.3.exe", desktop.Url);
        Assert.Equal(desktopBytes.Length, desktop.Size);
        Assert.Equal(Sha256Hex(desktopBytes), desktop.Sha256);
        Assert.True(desktop.ModifiedAt > 0);

        var android = Assert.IsType<ReleaseArtifact>(manifest.Artifacts.Android);
        Assert.Equal("EriseChat-1.2.3.apk", android.FileName);
        Assert.Equal(Sha256Hex(androidBytes), android.Sha256);
    }

    [Fact]
    public async Task GetManifest_MultipleVersions_PicksNewestByLastWriteTime()
    {
        WriteFile("EriseChat-Setup-1.2.3.exe", [1], DateTime.UtcNow.AddMinutes(-5));
        WriteFile("EriseChat-Setup-1.3.0.exe", [2], DateTime.UtcNow);

        var manifest = await CreateService().GetManifestAsync(protocolVersion: 1);

        Assert.Equal("1.3.0", manifest.Version);
        Assert.Equal("EriseChat-Setup-1.3.0.exe", manifest.Artifacts.Desktop?.FileName);
    }

    [Fact]
    public async Task GetManifest_InvalidFileNames_AreIgnored()
    {
        WriteFile("EriseChat-Setup-beta.exe", [1]);
        WriteFile("EriseChat-2.0.apk", [2]);
        WriteFile("other.exe", [3]);

        var manifest = await CreateService().GetManifestAsync(protocolVersion: 1);

        Assert.Equal(ReleaseArtifactService.ServerVersion(), manifest.Version);
        Assert.Null(manifest.Artifacts.Desktop);
        Assert.Null(manifest.Artifacts.Android);
    }

    [Fact]
    public async Task GetManifest_Sha256IsCachedAndInvalidated_WhenFileChanges()
    {
        var path = WriteFile("EriseChat-Setup-1.2.3.exe", [1, 2, 3]);
        var service = CreateService();

        var first = await service.GetManifestAsync(protocolVersion: 1);
        Assert.Equal(Sha256Hex([1, 2, 3]), first.Artifacts.Desktop?.Sha256);

        File.WriteAllBytes(path, [4, 5, 6, 7]);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));

        var second = await service.GetManifestAsync(protocolVersion: 1);
        Assert.Equal(Sha256Hex([4, 5, 6, 7]), second.Artifacts.Desktop?.Sha256);
        Assert.NotEqual(first.Artifacts.Desktop?.Sha256, second.Artifacts.Desktop?.Sha256);
    }

    [Fact]
    public async Task GetManifest_UsesHigherArtifactVersionAsManifestVersion()
    {
        WriteFile("EriseChat-Setup-9.9.9.exe", [1]);

        var manifest = await CreateService().GetManifestAsync(protocolVersion: 1);

        Assert.Equal("9.9.9", manifest.Version);
        Assert.Equal("9.9.9", manifest.Artifacts.Desktop?.Version);
    }

    private ReleaseArtifactService CreateService() => new(_root, NullLogger<ReleaseArtifactService>.Instance);

    private string WriteFile(string fileName, byte[] bytes, DateTime? lastWriteUtc = null)
    {
        var path = Path.Combine(_root, fileName);
        File.WriteAllBytes(path, bytes);
        if (lastWriteUtc is { } stamp)
        {
            File.SetLastWriteTimeUtc(path, stamp);
        }

        return path;
    }

    private static string Sha256Hex(byte[] bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
