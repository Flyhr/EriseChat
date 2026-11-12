using System.Security.Cryptography;
using System.Text.Json;
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

/// <summary>
/// 客户端更新清单：GET /api/version 的响应体（字段见 protocol/protocol.md §7 第 10 条）。
/// 2026-11-12 追加可选字段 webVersion / minVersion / releaseNotes / publishedAt，原有字段结构不变。
/// </summary>
public sealed record ReleaseManifest(
    string Version,
    int ProtocolVersion,
    ReleaseArtifacts Artifacts,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? WebVersion,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? MinVersion,
    string[] ReleaseNotes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] long? PublishedAt);

/// <summary>
/// 安装包产物清单（F-29）：扫描下载目录中最新的 Windows exe / Android apk，计算 sha256 并在进程内缓存。
/// 缓存键 = 完整路径 + 最后写入时间 + 大小，文件一变化自动失效；只认三段数字版本号的文件名，
/// 临时文件或手工改名文件不会被当成正式产物。
/// 2026-11-12：额外读取随 Web 部署的 web-version.json 与下载目录的 release.json，
/// 只有发行版本与 Web、两个平台产物版本全部一致时才下发 minVersion / releaseNotes / publishedAt。
/// </summary>
public sealed class ReleaseArtifactService
{
    public const string DesktopSearchPattern = "EriseChat-Setup-*.exe";
    public const string AndroidSearchPattern = "EriseChat-*.apk";
    public const string WebVersionFileName = "web-version.json";
    public const string ReleaseFileName = "release.json";

    private const int MaxReleaseNotes = 10;
    private const int MaxReleaseNoteLength = 200;

    private static readonly Regex DesktopNamePattern = new(
        @"^EriseChat-Setup-(?<version>\d+\.\d+\.\d+)\.exe$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AndroidNamePattern = new(
        @"^EriseChat-(?<version>\d+\.\d+\.\d+)\.apk$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex VersionPattern = new(
        @"^\d+\.\d+\.\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly string _downloadDirectory;
    private readonly string _webRootPath;
    private readonly ILogger<ReleaseArtifactService> _logger;
    private readonly Dictionary<string, string> _sha256Cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _cacheLock = new();

    public ReleaseArtifactService(
        string downloadDirectory,
        string webRootPath,
        ILogger<ReleaseArtifactService> logger)
    {
        _downloadDirectory = Path.GetFullPath(downloadDirectory);
        _webRootPath = Path.GetFullPath(webRootPath);
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
        var webVersion = ReadWebVersion();
        var metadata = ReadReleaseMetadata(desktop, android, webVersion);
        _logger.LogDebug(
            "更新清单：version={Version}，webVersion={WebVersion}，desktop={Desktop}，android={Android}，minVersion={MinVersion}",
            version,
            webVersion ?? "无",
            desktop?.FileName ?? "无",
            android?.FileName ?? "无",
            metadata.MinVersion ?? "无");
        return new ReleaseManifest(
            version,
            protocolVersion,
            new ReleaseArtifacts(desktop, android),
            webVersion,
            metadata.MinVersion,
            metadata.ReleaseNotes,
            metadata.PublishedAt);
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

    /// <summary>读取随当前 Web 构建发布的版本文件；缺失或非法时返回 null（浏览器端不提示刷新）。</summary>
    private string? ReadWebVersion()
    {
        var path = Path.Combine(_webRootPath, WebVersionFileName);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var version = ReadString(document.RootElement, "version");
            return IsVersion(version) ? version : null;
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(error, "Web 版本文件不可用，按缺失处理");
            return null;
        }
    }

    /// <summary>
    /// 读取下载目录中的发行元数据。只有 release.json 的 version 与当前 Web 及两个平台产物版本全部一致时
    /// 才返回门槛与说明；文件缺失、无效或不一致一律按「无门槛、无说明」处理。
    /// </summary>
    private ReleaseMetadata ReadReleaseMetadata(
        ReleaseArtifact? desktop,
        ReleaseArtifact? android,
        string? webVersion)
    {
        if (webVersion is null || desktop is null || android is null)
        {
            return ReleaseMetadata.Empty;
        }

        var path = Path.Combine(_downloadDirectory, ReleaseFileName);
        if (!File.Exists(path))
        {
            return ReleaseMetadata.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return ReleaseMetadata.Empty;
            }

            var root = document.RootElement;
            var version = ReadString(root, "version");
            if (!IsVersion(version) ||
                !string.Equals(version, webVersion, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(version, desktop.Version, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(version, android.Version, StringComparison.OrdinalIgnoreCase))
            {
                return ReleaseMetadata.Empty;
            }

            // 门槛必须合法且不高于发行版本，否则按未设置处理。
            var minVersion = ReadString(root, "minVersion");
            if (minVersion is not null &&
                (!IsVersion(minVersion) || CompareVersions(minVersion, version!) > 0))
            {
                minVersion = null;
            }

            return new ReleaseMetadata(minVersion, ReadReleaseNotes(root), ReadTimestamp(root, "publishedAt"));
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogDebug(error, "发行元数据不可用，按无门槛处理");
            return ReleaseMetadata.Empty;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var text = value.GetString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string[] ReadReleaseNotes(JsonElement root)
    {
        if (!root.TryGetProperty("releaseNotes", out var notes) || notes.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<string>();
        }

        var list = new List<string>();
        foreach (var item in notes.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var text = item.GetString()?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            list.Add(text.Length > MaxReleaseNoteLength ? text[..MaxReleaseNoteLength] : text);
            if (list.Count >= MaxReleaseNotes)
            {
                break;
            }
        }

        return list.ToArray();
    }

    private static long? ReadTimestamp(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out var milliseconds) ||
            milliseconds <= 0)
        {
            return null;
        }

        return milliseconds;
    }

    private static bool IsVersion(string? value) =>
        !string.IsNullOrWhiteSpace(value) && VersionPattern.IsMatch(value);

    private static int CompareVersions(string left, string right) =>
        Version.TryParse(left, out var a) && Version.TryParse(right, out var b) ? a.CompareTo(b) : 0;

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

    /// <summary>发行元数据；Empty 表示不设门槛、无说明（文件缺失、无效或版本不一致）。</summary>
    private readonly record struct ReleaseMetadata(string? MinVersion, string[] ReleaseNotes, long? PublishedAt)
    {
        public static ReleaseMetadata Empty => new(null, Array.Empty<string>(), null);
    }
}
