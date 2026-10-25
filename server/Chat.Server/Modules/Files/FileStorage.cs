using System.Globalization;
using Chat.Server.Common.Options;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Files;

/// <summary>
/// 文件磁盘存储层（M3-02）：根目录 = <see cref="AppOptions.FileDirectory"/>，
/// 按 <c>yyyyMM/{fileId}{ext}</c> 存放（protocol/protocol.md §7）。存库的只有相对根目录的路径。
/// 所有路径解析后都校验位于根目录内，防 <c>../</c> 路径穿越。
/// </summary>
public sealed class FileStorage
{
    private readonly string _rootDirectory;
    private readonly ILogger<FileStorage> _logger;
    private readonly object _sync = new();

    public FileStorage(IOptions<AppOptions> options, ILogger<FileStorage> logger)
    {
        _logger = logger;
        var configured = options.Value.FileDirectory;
        _rootDirectory = Path.GetFullPath(
            string.IsNullOrWhiteSpace(configured) ? "../../data/files" : configured);
        Directory.CreateDirectory(_rootDirectory);
    }

    /// <summary>存储根目录（绝对路径，仅服务端内部使用，不返回给客户端）。</summary>
    public string RootDirectory => _rootDirectory;

    /// <summary>
    /// 保存文件流：生成 fileId（UUID 文本）→ 写入 <c>yyyyMM/{fileId}{ext}</c>。
    /// 返回 fileId 与相对根目录的 storagePath（如 <c>202610/{id}.png</c>，统一用 '/'）。
    /// </summary>
    public async Task<(string FileId, string StoragePath)> SaveAsync(
        Stream content,
        string extension,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var normalizedExtension = NormalizeExtension(extension);
        var fileId = Guid.NewGuid().ToString("D");
        var month = DateTimeOffset.UtcNow.ToString("yyyyMM", CultureInfo.InvariantCulture);
        var storagePath = $"{month}/{fileId}{normalizedExtension}";
        var fullPath = ResolveFullPath(storagePath);

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);

        await using (var target = new FileStream(
                         fullPath,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         bufferSize: 81920,
                         useAsync: true))
        {
            await content.CopyToAsync(target, cancellationToken);
        }

        _logger.LogDebug("文件已落盘：{StoragePath}（{Bytes} 字节）", storagePath, new FileInfo(fullPath).Length);
        return (fileId, storagePath);
    }

    /// <summary>只读打开（调用方负责释放）；文件不存在抛 <see cref="FileNotFoundException"/>。</summary>
    public Stream OpenRead(string storagePath)
        => new FileStream(ResolveFullPath(storagePath), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>删除磁盘文件；文件不存在视为成功（幂等，供孤儿清理使用）。</summary>
    public void Delete(string storagePath)
    {
        var fullPath = ResolveFullPath(storagePath);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
    }

    /// <summary>读取磁盘文件大小（上传落盘后复核实际字节数用）；文件不存在抛异常。</summary>
    public long GetLength(string storagePath) => new FileInfo(ResolveFullPath(storagePath)).Length;

    /// <summary>
    /// 把相对根目录的 storagePath 解析为绝对路径，并确保仍在根目录内（防路径穿越）。
    /// </summary>
    public string ResolveFullPath(string storagePath)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("storagePath 不能为空", nameof(storagePath));
        }

        var relative = storagePath.Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relative))
        {
            throw new InvalidOperationException("storagePath 必须是相对根目录的路径");
        }

        var fullPath = Path.GetFullPath(Path.Combine(_rootDirectory, relative));
        var rootWithSeparator = _rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? _rootDirectory
            : _rootDirectory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("storagePath 超出了文件根目录");
        }

        return fullPath;
    }

    private static string NormalizeExtension(string extension)
    {
        var value = (extension ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length is < 2 or > 10 || !value.StartsWith('.'))
        {
            throw new ArgumentException("扩展名不合法", nameof(extension));
        }

        foreach (var character in value.AsSpan(1))
        {
            if (!char.IsLetterOrDigit(character))
            {
                throw new ArgumentException("扩展名不合法", nameof(extension));
            }
        }

        return value;
    }
}
