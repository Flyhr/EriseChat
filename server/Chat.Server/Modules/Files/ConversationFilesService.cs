using System.Text.Json;
using System.Text.RegularExpressions;
using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Modules.Files;

/// <summary>共享文件条目（与 apps/web/src/api/conversations.ts 的 SharedFileItem 对齐，F-15）。</summary>
public sealed record ConversationFileItem(
    string FileId,
    string Name,
    long Size,
    string Mime,
    string MessageId,
    long MessageSeq,
    string SenderId,
    long CreatedAt);

/// <summary>共享文件分页（items 按 messageSeq 降序；hasMore 表示还有更早的引用）。</summary>
public sealed record ConversationFilesPage(IReadOnlyList<ConversationFileItem> Items, bool HasMore);

/// <summary>共享链接条目（C2c）：一条链接及其所在的 text 消息，按消息 seq 倒序分页。</summary>
public sealed record ConversationLinkItem(
    string Url,
    string MessageId,
    long MessageSeq,
    string SenderId,
    long CreatedAt);

/// <summary>共享链接分页（items 按 messageSeq 降序；hasMore 表示还有更早的链接）。</summary>
public sealed record ConversationLinksPage(IReadOnlyList<ConversationLinkItem> Items, bool HasMore);

/// <summary>
/// 会话共享文件汇总（F-15）：只统计 messages.file_id 非空的 image/file 消息，
/// 按文件去重取最新一条引用消息（max seq），以该 seq 作为 beforeSeq 游标倒序分页。
/// 只读接口，不新增表/列/帧；头像与未发送的上传文件天然不在消息引用中，因此不会出现。
/// 语音消息（F-28，voice）不进入汇总：type 仅接受 image/file，语音文件不会出现在共享文件列表。
/// </summary>
public sealed class ConversationFilesService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;
    private const int MaxIdLength = 64;
    private const int FileIdLogPrefixLength = 6;
    /// <summary>链接汇总单页最多扫描的消息数，避免链接稀疏时全表扫描。</summary>
    private const int MaxLinkScanWindow = 400;

    /// <summary>HTTP/HTTPS URL 识别（与消息搜索 linkOnly 的识别规则一致）。</summary>
    private static readonly Regex HttpUrlPattern = new(
        @"https?://[^\s<>`）】]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly ChatDbContext _db;
    private readonly ILogger<ConversationFilesService> _logger;

    public ConversationFilesService(ChatDbContext db, ILogger<ConversationFilesService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// 分页查询会话内共享图片/文件：会话不存在 404、非成员 403、非法 type/limit/beforeSeq 400。
    /// </summary>
    public async Task<ConversationFilesPage> GetSharedFilesAsync(
        string userId,
        string conversationId,
        string? type,
        long? beforeSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        var id = await EnsureConversationMemberAsync(userId, conversationId, cancellationToken);

        var messageType = ParseType(type);
        var pageSize = NormalizeLimit(limit);
        if (beforeSeq is <= 0)
        {
            throw ApiException.ValidationFailed("beforeSeq 必须是正整数");
        }

        // 1) 按文件去重取最新引用 seq，倒序后再按 beforeSeq 过滤。
        // 注意：必须「先聚合出每个文件的 max seq，再过滤游标」，否则同一文件在旧页出现过引用时
        // 下一页会以更早的 seq 再次出现（重复项）。
        // 撤回消息不再计入共享列表（FileId 仍保留在行上供孤儿清理，只能在消息层过滤）。
        var groups = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == id && message.FileId != null &&
                              message.MsgType == messageType && message.DeletedAt == null)
            .GroupBy(message => message.FileId!)
            .Select(group => new { FileId = group.Key, Seq = group.Max(message => message.Seq) })
            .OrderByDescending(item => item.Seq)
            .ToListAsync(cancellationToken);

        var filteredGroups = beforeSeq is null
            ? groups
            : groups.Where(item => item.Seq < beforeSeq.Value).ToList();
        var hasMore = filteredGroups.Count > pageSize;
        var page = hasMore ? filteredGroups.Take(pageSize).ToList() : filteredGroups;
        if (page.Count == 0)
        {
            return new ConversationFilesPage(Array.Empty<ConversationFileItem>(), false);
        }

        // 2) 取引用消息行（messageId/senderId/createdAt）与文件元数据（name/size/mime）。
        var pageSeqs = page.Select(item => item.Seq).ToList();
        var messageRows = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == id && message.FileId != null && pageSeqs.Contains(message.Seq))
            .ToListAsync(cancellationToken);
        var messageBySeq = messageRows.ToDictionary(message => message.Seq);

        var fileIds = page.Select(item => item.FileId).Distinct().ToList();
        var files = await _db.Files.AsNoTracking()
            .Where(file => fileIds.Contains(file.Id))
            .ToDictionaryAsync(file => file.Id, cancellationToken);

        var items = new List<ConversationFileItem>(page.Count);
        foreach (var group in page)
        {
            if (!messageBySeq.TryGetValue(group.Seq, out MessageEntity? message) ||
                !files.TryGetValue(group.FileId, out var file))
            {
                // 理论不会发生（支撑列与 files 行同事务写入）；只记 fileId 前缀，不记文件名。
                _logger.LogWarning(
                    "共享文件汇总跳过缺失行：{FileIdPrefix}",
                    group.FileId.Length > FileIdLogPrefixLength
                        ? group.FileId[..FileIdLogPrefixLength]
                        : group.FileId);
                continue;
            }

            items.Add(new ConversationFileItem(
                file.Id,
                file.OriginalName,
                file.Size,
                file.Mime,
                message.Id,
                message.Seq,
                message.SenderId,
                message.CreatedAt));
        }

        return new ConversationFilesPage(items, hasMore);
    }

    /// <summary>
    /// 分页查询会话内共享链接（C2c）：从 text 消息里提取 HTTP(S) 链接，按消息 seq 倒序。
    /// 会话不存在 404、非成员 403、非法 beforeSeq/limit 400；单页最多扫描 <see cref="MaxLinkScanWindow"/> 条候选消息。
    /// </summary>
    public async Task<ConversationLinksPage> GetSharedLinksAsync(
        string userId,
        string conversationId,
        long? beforeSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        var id = await EnsureConversationMemberAsync(userId, conversationId, cancellationToken);
        var pageSize = NormalizeLimit(limit);
        if (beforeSeq is <= 0)
        {
            throw ApiException.ValidationFailed("beforeSeq 必须是正整数");
        }

        var scanWindow = Math.Max(pageSize, Math.Min(pageSize * 4, MaxLinkScanWindow));
        var query = _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == id && message.MsgType == MessageType.Text &&
                              message.DeletedAt == null && message.ContentJson.Contains("http"));
        if (beforeSeq is not null)
        {
            query = query.Where(message => message.Seq < beforeSeq.Value);
        }

        var rows = await query
            .OrderByDescending(message => message.Seq)
            .Take(scanWindow + 1)
            .ToListAsync(cancellationToken);
        var truncated = rows.Count > scanWindow;
        if (truncated)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var items = new List<ConversationLinkItem>(pageSize);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasMore = false;
        foreach (var message in rows)
        {
            foreach (var url in ExtractHttpUrls(message.ContentJson))
            {
                if (!seen.Add(url))
                {
                    continue;
                }

                if (items.Count >= pageSize)
                {
                    hasMore = true;
                    break;
                }

                items.Add(new ConversationLinkItem(url, message.Id, message.Seq, message.SenderId, message.CreatedAt));
            }

            if (hasMore)
            {
                break;
            }
        }

        // 没有条目时不回报 hasMore：客户端没有下一页游标，空页 + hasMore 会陷入重复请求。
        return new ConversationLinksPage(items, (hasMore || truncated) && items.Count > 0);
    }

    /// <summary>校验会话存在且当前用户是成员，返回 trim 后的会话 ID（共享文件/链接共用）。</summary>
    private async Task<string> EnsureConversationMemberAsync(
        string userId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var id = (conversationId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > MaxIdLength)
        {
            throw ApiException.ValidationFailed("conversationId 不合法");
        }

        var exists = await _db.Conversations.AsNoTracking()
            .AnyAsync(conversation => conversation.Id == id, cancellationToken);
        if (!exists)
        {
            throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        var isMember = await _db.ConversationMembers.AsNoTracking()
            .AnyAsync(
                member => member.ConversationId == id && member.UserId == userId,
                cancellationToken);
        if (!isMember)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);
        }

        return id;
    }

    /// <summary>从 text 消息的 contentJson 中提取 HTTP(S) 链接，去掉尾部标点（与消息搜索 linkOnly 口径一致）。</summary>
    private static IEnumerable<string> ExtractHttpUrls(string contentJson)
    {
        string? text;
        try
        {
            text = JsonSerializer.Deserialize<string>(contentJson);
        }
        catch (JsonException)
        {
            yield break;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            yield break;
        }

        foreach (Match match in HttpUrlPattern.Matches(text))
        {
            var url = match.Value.TrimEnd(
                '.', ',', ';', ':', '!', '?', ')', ']', '}', '>', '"', '\'', '。', '，', '；', '：', '！', '？', '）', '】', '》');
            if (url.Length <= "https://".Length)
            {
                continue;
            }

            yield return url;
        }
    }

    private static MessageType ParseType(string? type)
    {
        switch ((type ?? string.Empty).Trim().ToLowerInvariant())
        {
            case "image":
                return MessageType.Image;
            case "file":
                return MessageType.File;
            default:
                throw ApiException.ValidationFailed("type 只能是 image 或 file");
        }
    }

    private static int NormalizeLimit(int? limit)
    {
        // 缺省（query 未提供）取默认 50；显式传 0 属于非法值，按 400 处理。
        if (limit is null)
        {
            return DefaultLimit;
        }

        if (limit is < 1 or > MaxLimit)
        {
            throw ApiException.ValidationFailed($"limit 必须是 1~{MaxLimit} 的整数");
        }

        return limit.Value;
    }
}
