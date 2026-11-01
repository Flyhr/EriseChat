using System.Text;
using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Server.Modules.Files;

/// <summary>上传成功结果（响应字段与 apps/web/src/api/files.ts 的 UploadFileResponse 对齐）。</summary>
public sealed record UploadFileResult(string FileId, string Name, long Size, string Mime, long CreatedAt);

/// <summary>下载结果：文件元数据 + 已打开的只读流（调用方负责释放）。</summary>
public sealed record DownloadFileResult(StoredFile File, Stream Content);

/// <summary>
/// 文件业务（M3-02）：上传校验（白名单 + 魔数 + 大小）、落盘落库、下载鉴权（上传者本人或引用消息所在会话成员）。
/// 二进制文件不经 WebSocket；此处不新增协议错误码，复用 FILE_TYPE_NOT_ALLOWED / FILE_TOO_LARGE 等现有码。
/// </summary>
public sealed class FileService
{
    private const int MaxOriginalNameLength = 255;
    private const int MaxFileIdLength = 64;
    private const int MagicHeadLength = 16;

    /// <summary>扩展名白名单 → MIME（不信任客户端 Content-Type，MIME 由服务端按扩展名决定）。</summary>
    private static readonly IReadOnlyDictionary<string, string> AllowedExtensions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".webm"] = "audio/webm",
            [".pdf"] = "application/pdf",
            [".txt"] = "text/plain",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".zip"] = "application/zip",
            [".7z"] = "application/x-7z-compressed",
        };

    private static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
    private static readonly byte[] Gif87Signature = "GIF87a"u8.ToArray();
    private static readonly byte[] Gif89Signature = "GIF89a"u8.ToArray();
    private static readonly byte[] RiffSignature = "RIFF"u8.ToArray();
    private static readonly byte[] WebpSignature = "WEBP"u8.ToArray();
    private static readonly byte[] PdfSignature = "%PDF"u8.ToArray();

    /// <summary>WebM/Matroska 容器的 EBML 头（语音消息 .webm 魔数，F-28）。</summary>
    private static readonly byte[] WebmSignature = [0x1A, 0x45, 0xDF, 0xA3];

    private readonly ChatDbContext _db;
    private readonly FileStorage _storage;
    private readonly AppOptions _options;
    private readonly ILogger<FileService> _logger;

    public FileService(
        ChatDbContext db,
        FileStorage storage,
        IOptions<AppOptions> options,
        ILogger<FileService> logger)
    {
        _db = db;
        _storage = storage;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// 上传：校验扩展名白名单/大小/魔数 → 落盘 → 写 files 行。
    /// 原始文件名只入库展示，不参与磁盘路径；失败时清理已落盘文件。
    /// </summary>
    public async Task<UploadFileResult> UploadAsync(
        string userId,
        string? originalName,
        Stream content,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        var name = NormalizeOriginalName(originalName);
        var extension = Path.GetExtension(name).ToLowerInvariant();
        if (!AllowedExtensions.TryGetValue(extension, out var mime))
        {
            throw new ApiException(
                ErrorCodes.FileTypeNotAllowed,
                "不支持的文件类型",
                StatusCodes.Status400BadRequest);
        }

        if (declaredLength <= 0)
        {
            throw ApiException.ValidationFailed("文件为空");
        }

        if (declaredLength > _options.UploadMaxBytes)
        {
            throw new ApiException(
                ErrorCodes.FileTooLarge,
                $"文件超过上传上限（最大 {_options.UploadMaxBytes / 1024 / 1024}MB）",
                StatusCodes.Status413PayloadTooLarge);
        }

        var head = new byte[(int)Math.Min(MagicHeadLength, declaredLength)];
        var headLength = await ReadHeadAsync(content, head, cancellationToken);
        if (!HasValidSignature(extension, head.AsSpan(0, headLength)))
        {
            throw new ApiException(
                ErrorCodes.FileTypeNotAllowed,
                "文件内容与扩展名不匹配",
                StatusCodes.Status400BadRequest);
        }

        var (source, disposeSource) = PrepareSource(content, head.AsMemory(0, headLength));
        string? storagePath = null;
        try
        {
            var (fileId, savedPath) = await _storage.SaveAsync(source, extension, cancellationToken);
            storagePath = savedPath;

            var actualSize = _storage.GetLength(savedPath);
            if (actualSize != declaredLength)
            {
                throw ApiException.ValidationFailed("上传内容不完整，请重试");
            }

            var entity = new StoredFile
            {
                Id = fileId,
                OwnerId = userId,
                OriginalName = name,
                Mime = mime,
                Size = actualSize,
                StoragePath = savedPath,
                CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            };
            _db.Files.Add(entity);
            await _db.SaveChangesAsync(cancellationToken);

            _logger.LogInformation("文件上传成功：{FileId}，{Size} 字节，MIME={Mime}", entity.Id, entity.Size, entity.Mime);
            return new UploadFileResult(entity.Id, entity.OriginalName, entity.Size, entity.Mime, entity.CreatedAt);
        }
        catch
        {
            if (storagePath is not null)
            {
                TryDelete(storagePath, "上传失败回滚");
            }

            throw;
        }
        finally
        {
            if (disposeSource)
            {
                await source.DisposeAsync();
            }
        }
    }

    /// <summary>
    /// 下载鉴权：上传者本人、该文件被某条消息引用且请求者为该消息所在会话成员、
    /// 或该文件被任一用户用作头像（F-12：内部版头像对已登录用户可见）；否则 FORBIDDEN。
    /// </summary>
    public async Task<DownloadFileResult> DownloadAsync(string userId, string fileId, CancellationToken cancellationToken)
    {
        if (!IsSafeFileId(fileId))
        {
            throw ApiException.NotFound("文件不存在");
        }

        var file = await _db.Files.AsNoTracking().FirstOrDefaultAsync(item => item.Id == fileId, cancellationToken);
        if (file is null)
        {
            throw ApiException.NotFound("文件不存在");
        }

        var isOwner = string.Equals(file.OwnerId, userId, StringComparison.Ordinal);
        if (!isOwner)
        {
            var isReferencedMember = await _db.Messages.AsNoTracking()
                .Where(message => message.FileId == fileId)
                .AnyAsync(
                    message => _db.ConversationMembers.Any(
                        member => member.ConversationId == message.ConversationId && member.UserId == userId),
                    cancellationToken);
            if (!isReferencedMember)
            {
                // F-12/F-23：头像文件（个人头像或群头像）对已登录用户放行（内部版头像可见性，不开放给未登录请求）。
                var isAvatarFile = await _db.Users.AsNoTracking()
                        .AnyAsync(user => user.AvatarFileId == fileId, cancellationToken)
                    || await _db.Conversations.AsNoTracking()
                        .AnyAsync(conversation => conversation.AvatarFileId == fileId, cancellationToken);
                if (!isAvatarFile)
                {
                    throw ApiException.Forbidden("无权访问该文件");
                }
            }
        }

        Stream stream;
        try
        {
            stream = _storage.OpenRead(file.StoragePath);
        }
        catch (FileNotFoundException)
        {
            _logger.LogWarning("文件记录存在但磁盘文件缺失：{FileId}", file.Id);
            throw ApiException.NotFound("文件不存在");
        }
        catch (IOException exception)
        {
            _logger.LogWarning(exception, "打开文件失败：{FileId}", file.Id);
            throw;
        }

        return new DownloadFileResult(file, stream);
    }

    /// <summary>魔数最小校验：图片头、%PDF、zip 系 PK\x03\x04；txt/7z 不做强制（按 M3-02 范围）。</summary>
    private static bool HasValidSignature(string extension, ReadOnlySpan<byte> head) => extension switch
    {
        ".png" => head.Length >= PngSignature.Length && head[..PngSignature.Length].SequenceEqual(PngSignature),
        ".jpg" or ".jpeg" => head.Length >= 3 && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF,
        ".gif" => head.Length >= 6 &&
                 (head[..6].SequenceEqual(Gif87Signature) || head[..6].SequenceEqual(Gif89Signature)),
        ".webp" => head.Length >= 12 &&
                   head[..4].SequenceEqual(RiffSignature) &&
                   head.Slice(8, 4).SequenceEqual(WebpSignature),
        ".pdf" => head.Length >= 4 && head[..4].SequenceEqual(PdfSignature),
        ".webm" => head.Length >= 4 && head[..4].SequenceEqual(WebmSignature),
        ".zip" or ".docx" or ".xlsx" or ".pptx" =>
            head.Length >= 4 && head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04,
        _ => true,
    };

    private static async Task<int> ReadHeadAsync(Stream content, byte[] head, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < head.Length)
        {
            var count = await content.ReadAsync(head.AsMemory(read, head.Length - read), cancellationToken);
            if (count == 0)
            {
                break;
            }

            read += count;
        }

        return read;
    }

    /// <summary>
    /// 头部已读后准备完整源流：可 seek 的流回退位置即可；否则把头部 + 剩余内容拼进内存流（受大小上限约束）。
    /// </summary>
    private static (Stream Source, bool DisposeSource) PrepareSource(Stream content, ReadOnlyMemory<byte> head)
    {
        if (content.CanSeek)
        {
            content.Position -= head.Length;
            return (content, false);
        }

        var buffered = new MemoryStream();
        buffered.Write(head.Span);
        content.CopyTo(buffered);
        buffered.Position = 0;
        return (buffered, true);
    }

    private static string NormalizeOriginalName(string? originalName)
    {
        var fileName = Path.GetFileName((originalName ?? string.Empty).Trim());
        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw ApiException.ValidationFailed("文件名不能为空");
        }

        if (fileName.Length > MaxOriginalNameLength)
        {
            throw ApiException.ValidationFailed($"文件名过长（最多 {MaxOriginalNameLength} 个字符）");
        }

        return fileName;
    }

    private static bool IsSafeFileId(string fileId)
    {
        if (string.IsNullOrWhiteSpace(fileId) || fileId.Length > MaxFileIdLength)
        {
            return false;
        }

        foreach (var character in fileId)
        {
            if (!char.IsLetterOrDigit(character) && character is not ('-' or '_'))
            {
                return false;
            }
        }

        return true;
    }

    private void TryDelete(string storagePath, string reason)
    {
        try
        {
            _storage.Delete(storagePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "清理磁盘文件失败（{Reason}）：{StoragePath}", reason, storagePath);
        }
    }
}
