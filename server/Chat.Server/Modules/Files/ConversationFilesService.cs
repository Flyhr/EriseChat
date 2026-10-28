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

/// <summary>
/// 会话共享文件汇总（F-15）：只统计 messages.file_id 非空的 image/file 消息，
/// 按文件去重取最新一条引用消息（max seq），以该 seq 作为 beforeSeq 游标倒序分页。
/// 只读接口，不新增表/列/帧；头像与未发送的上传文件天然不在消息引用中，因此不会出现。
/// </summary>
public sealed class ConversationFilesService
{
    private const int DefaultLimit = 50;
    private const int MaxLimit = 100;
    private const int MaxIdLength = 64;
    private const int FileIdLogPrefixLength = 6;

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
        var id = (conversationId ?? string.Empty).Trim();
        if (id.Length == 0 || id.Length > MaxIdLength)
        {
            throw ApiException.ValidationFailed("conversationId 不合法");
        }

        var messageType = ParseType(type);
        var pageSize = NormalizeLimit(limit);
        if (beforeSeq is <= 0)
        {
            throw ApiException.ValidationFailed("beforeSeq 必须是正整数");
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

        // 1) 按文件去重取最新引用 seq，倒序后再按 beforeSeq 过滤。
        // 注意：必须「先聚合出每个文件的 max seq，再过滤游标」，否则同一文件在旧页出现过引用时
        // 下一页会以更早的 seq 再次出现（重复项）。
        var groups = await _db.Messages.AsNoTracking()
            .Where(message => message.ConversationId == id && message.FileId != null && message.MsgType == messageType)
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
