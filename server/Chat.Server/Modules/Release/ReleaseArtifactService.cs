using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Chat.Server.Modules.Release;

/// <summary>单个平台的安装包产物（F-29，protocol/protocol.md §7 第 10 条）。</summary>
public sealed record ReleaseArtifact(
    string Version,
    string FileName,
    string Url,
    long Size,
    string Sha256,
    long ModifiedAt);

/// <summary>清单中的产物分组（F-29，protocol/protocol.md §7 第 10 条：artifacts.desktop / artifacts.android）。</summary>
public sealed record ReleaseArtifacts(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ReleaseArtifact? Desktop,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] ReleaseArtifact? Android);

/// <summary>客户端更新清单：GET /api/version 的响应体（字段见 protocol/protocol.md §7 第 10 条）。</summary>
public sealed record ReleaseManifest(
    string Version,
    int ProtocolVersion,
    ReleaseArtifacts Artifacts);

/// <summary>
/// 安装包产物清单（F-29）：扫描下载目录中最新的 Windows exe / Android apk，计算 sha256 并在进程内缓存。
/// 缓存键 = 完整路径 + 最后写入时间 + 大小，文件一变化自动失效；只认三段数字版本号的文件名，
/// 临时文件或手工改名文件不会被当成正式产物。
/// </summary>
public sealed class ReleaseArtifactService
{
    public const string DesktopSearchPattern = "EiseChat-Setup-*.exe";
    public const string AndroidSearchPattern = "EiseChat-*.apk";

    private static readonly Regex DesktopNamePattern = new(
        @"^EiseChat-Setup-(?<version>\d+\.\d+\.\d+)\.exe$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AndroidNamePattern = new(
        @"^EiseChat-(?<version>\d+\.\d+\.\d+)\.apk$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly string _downloadDirectory;
    private readonly ILogger<ReleaseArtifactService> _logger;
    private readonly Dictionary<string, string> _sha256Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public ReleaseArtifactService(string downloadDirectory, ILogger<ReleaseArtifactService> logger)
    {
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        _logger = logger;
    }

    /// <summary>生成更新清单：version 取服务端程序集版本与两个产物文件名版本的最大值。</summary>
    public async Task<ReleaseManifest> GetManifestAsync(int protocolVersion, CancellationToken cancellationToken = default)
    {
        var desktopFile = FindNewest(DesktopSearchPattern, DesktopNamePattern);
        var androidFile = FindNewest(AndroidSearchPattern, AndroidNamePattern);
        var desktop = desktopFile is null ? null : await ToArtifactAsync(desktopFile, DesktopNamePattern, cancellationToken);
        var android = androidFile is null ? null : await ToArtifactAsync(androidFile, AndroidNamePattern, cancellationToken);
        var version = MaxVersion(ServerVersion(), desktop?.Version, android?.Version);
        _logger.LogDebug(
            "更新清单：version={Version}，desktop={Desktop}，android={Android}",
            version,
            desktop?.FileName ?? "无",
            android?.FileName ?? "无");
        return new ReleaseManifest(version, protocolVersion, new ReleaseArtifacts(desktop, android));
    }

    /// <summary>服务端程序集版本（三段式），用于与安装包版本比较。</summary>
    public static string ServerVersion()
    {
        var version = typeof(ReleaseArtifactService).Assembly.GetName().Version;
        return version is null ? "0.0.0" : version.ToString(3);
    }

    private async Task<ReleaseArtifact> ToArtifactAsync(FileInfo file, Regex namePattern, CancellationToken cancellationToken)
    {
        var version = namePattern.Match(file.Name).Groups["version"].Value;
        var sha256 = await ComputeSha256Async(file, cancellationToken);
        file.Refresh();
        var modifiedAt = new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero).ToUnixTimeMilliseconds();
        return new ReleaseArtifact(
            version,
            file.Name,
            "/download/files/" + Uri.EscapeDataString(file.Name),
            file.Length,
            sha256,
            modifiedAt);
    }

    private async Task<string> ComputeSha256Async(FileInfo file, CancellationToken cancellationToken)
    {
        var cacheKey = $"{file.FullName}|{file.LastWriteTimeUtc.Ticks}|{file.Length}";
        lock (_cacheLock)
        {
            if (_sha256Cache.TryGetValue(cacheKey, out var cached))
            {
                return cached;
            }
        }

        await using var stream = new FileStream(
            file.FullName,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 64 * 1024,
            useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        var text = Convert.ToHexString(hash).ToLowerInvariant();

        lock (_cacheLock)
        {
            // 同一文件只保留最新一次摘要，避免文件多次更新后缓存膨胀。
            var prefix = file.FullName + "|";
            foreach (var staleKey in _sha256Cache.Keys
                         .Where(key => key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                         .ToArray())
            {
                _sha256Cache.Remove(staleKey);
            }

            _sha256Cache[cacheKey] = text;
        }

        return text;
    }

    /// <summary>同平台取最近修改的一个合法产物；文件名版本号不合法的文件直接忽略。</summary>
    private FileInfo? FindNewest(string searchPattern, Regex strictNamePattern)
    {
        var directory = new DirectoryInfo(_downloadDirectory);
        if (!directory.Exists)
        {
            return null;
        }

        return directory
            .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
            .Where(file => strictNamePattern.IsMatch(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    private static string MaxVersion(params string?[] versions)
    {
        Version? best = null;
        var bestText = "0.0.0";
        foreach (var text in versions)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                !Version.TryParse(text, out var parsed) ||
                (best is not null && parsed <= best))
            {
                continue;
            }

            best = parsed;
            bestText = text;
        }

        return bestText;
    }
}
