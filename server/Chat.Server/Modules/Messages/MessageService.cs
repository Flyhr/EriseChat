using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using MessageEntity = Chat.Server.Data.Entities.Message;
using ProtocolMessage = Chat.Protocol.Frames.Message;

namespace Chat.Server.Modules.Messages;

/// <summary>message.send 落库结果：协议消息 + 会话成员（用于广播）+ 是否新落库（幂等重发为 false）。</summary>
public sealed record MessageSendResult(ProtocolMessage Message, IReadOnlyList<string> MemberUserIds, bool Created);

/// <summary>sync.request 补拉结果（reqId 由 Realtime 层回填）。</summary>
public sealed record SyncPullResult(IReadOnlyList<SyncBatch> Batches, bool HasMore);

/// <summary>REST 历史分页结果（与 apps/web/src/api/messages.ts 的 MessagePage 对齐）。</summary>
public sealed record MessageHistoryPage(IReadOnlyList<ProtocolMessage> Messages, bool HasMore);

/// <summary>
/// REST 消息搜索结果（F-11，不属于协议帧）：messages 按 seq 降序；hasMore 表示还能继续往后翻候选。
/// 响应形状与 apps/web/src/api/messages.ts 的 MessageSearchPage 对齐。
/// </summary>
public sealed record MessageSearchPage(IReadOnlyList<ProtocolMessage> Messages, bool HasMore);

/// <summary>
/// 消息核心业务（M1-08，计划 §4.3/§4.4 + AGENTS §5）：
/// 1) message.send：成员校验 → 事务内分配严格 +1 seq → 落库（唯一约束幂等）→ 更新 last_message_id；
/// 2) sync.request：按游标补拉，每会话每批 ≤200 条、按 seq 升序；
/// 3) REST 历史分页：beforeSeq + limit（默认 50、上限 200），按 seq 升序返回；
/// 4) M3-03：image/file 消息校验 fileId 归属、content 以 files 记录归一，并落 messages.file_id。
/// </summary>
public sealed class MessageService
{
    /// <summary>文本消息 content 字符上限（超出按 MESSAGE_TOO_LARGE 拒绝，帧上限另有 256 KiB 保护）。</summary>
    public const int TextMaxLength = 4096;

    /// <summary>每会话每批补拉上限（protocol/protocol.md §4.4）。</summary>
    public const int SyncBatchSize = 200;

    /// <summary>REST 历史分页默认条数。</summary>
    public const int HistoryDefaultLimit = 50;

    /// <summary>REST 历史分页上限。</summary>
    public const int HistoryMaxLimit = 200;

    // F-11 消息搜索（REST，非协议帧）：q 长 1~50、limit 1~50（默认 20）。
    public const int SearchDefaultLimit = 20;
    public const int SearchMaxLimit = 50;
    public const int SearchQueryMaxLength = 50;

    /// <summary>最多扫描 4 个候选窗口（每窗口 min(limit*4, 200) 条），避免 LIKE 全表无限翻页。</summary>
    private const int SearchMaxCandidateRounds = 4;

    private const int SearchMaxCandidateWindow = 200;

    private const int MaxIdLength = 64;

    /// <summary>Reply 摘要文本上限（protocol/protocol.md §4.3：前 120 字符，超出追加省略号）。</summary>
    public const int ReplyTextMaxLength = 120;

    private readonly ChatDbContext _db;
    private readonly AppOptions _appOptions;

    public MessageService(ChatDbContext db, IOptions<AppOptions> appOptions)
    {
        _db = db;
        _appOptions = appOptions.Value;
    }

    /// <summary>
    /// 发送消息：参数/成员校验 → 幂等短路 → 事务内 seq 分配 + 落库 + 更新 last_message_id。
    /// 重复 (conversationId, clientMsgId) 不重复落库，返回原 messageId/seq/createdAt。
    /// </summary>
    public async Task<MessageSendResult> SendAsync(
        string senderId,
        MessageSendFrame request,
        CancellationToken cancellationToken)
    {
        var conversationId = ValidateId(request.ConversationId, "conversationId");
        var clientMsgId = ValidateId(request.ClientMsgId, "clientMsgId");
        // M3-03：image/file 需校验文件存在且属于发送者；content 的 name/size/mime 以 files 记录为准。
        var normalized = await NormalizeContentAsync(request, senderId, cancellationToken);
        var replyTo = string.IsNullOrWhiteSpace(request.ReplyTo) ? null : ValidateId(request.ReplyTo, "replyTo");

        // SQLite 默认延迟事务在并发写入时可能因锁升级冲突返回 SQLITE_BUSY / SQLITE_LOCKED（5/6），
        // busy_timeout 只覆盖等待写锁的场景；此处回滚后整体重试，保证多连接并发发送仍分配到唯一 seq。
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(senderId, conversationId, clientMsgId, replyTo, request, normalized, cancellationToken);
            }
            catch (Exception exception) when (attempt < maxAttempts && IsSqliteBusy(exception))
            {
                _db.ChangeTracker.Clear();
                await Task.Delay(25 * attempt, cancellationToken);
            }
        }
    }

    private async Task<MessageSendResult> SendOnceAsync(
        string senderId,
        string conversationId,
        string clientMsgId,
        string? replyTo,
        MessageSendFrame request,
        NormalizedSendContent normalized,
        CancellationToken cancellationToken)
    {
        var conversation = await EnsureMemberAsync(senderId, conversationId, cancellationToken);

        // F-09：replyTo 必须指向同会话内已存在的消息；保留实体用于组装引用摘要（不额外查询）。
        MessageEntity? replyEntity = null;
        if (replyTo is not null)
        {
            replyEntity = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(
                    message => message.Id == replyTo && message.ConversationId == conversationId,
                    cancellationToken);
            if (replyEntity is null)
            {
                throw ApiException.ValidationFailed("replyTo 指向的消息不存在或不属于该会话");
            }
        }

        // 幂等短路：同一会话内同一 clientMsgId 直接回执，不重复落库（协议 §4.3 规则 1）；重发同样带回引用摘要。
        var existing = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(
                message => message.ConversationId == conversationId && message.ClientMsgId == clientMsgId,
                cancellationToken);
        if (existing is not null)
        {
            var existingReply = await LoadReplyAsync(existing.ReplyTo, conversationId, cancellationToken);
            return new MessageSendResult(
                ToProtocolMessage(existing, ToOptionalReply(existingReply)),
                Array.Empty<string>(),
                Created: false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var seq = await SeqAllocator.AllocateAsync(_db, conversationId, cancellationToken);
            var entity = new MessageEntity
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = conversationId,
                SenderId = senderId,
                ClientMsgId = clientMsgId,
                Seq = seq,
                MsgType = normalized.MsgType,
                ContentJson = normalized.Content.Raw.GetRawText(),
                FileId = normalized.FileId,
                ReplyTo = replyTo,
                CreatedAt = now,
            };

            _db.Messages.Add(entity);
            conversation.LastMessageId = entity.Id;
            await _db.SaveChangesAsync(cancellationToken);

            var memberUserIds = await _db.ConversationMembers.AsNoTracking()
                .Where(member => member.ConversationId == conversationId)
                .Select(member => member.UserId)
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new MessageSendResult(
                ToProtocolMessage(entity, ToOptionalReply(replyEntity)),
                memberUserIds,
                Created: true);
        }
        catch (DbUpdateException)
        {
            // 并发重复发送：唯一索引 (conversation_id, client_msg_id) 兜底，回滚后按幂等回执。
            await transaction.RollbackAsync(CancellationToken.None);
            var raced = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(
                    message => message.ConversationId == conversationId && message.ClientMsgId == clientMsgId,
                    cancellationToken);
            if (raced is not null)
            {
                var racedReply = await LoadReplyAsync(raced.ReplyTo, conversationId, cancellationToken);
                return new MessageSendResult(
                    ToProtocolMessage(raced, ToOptionalReply(racedReply)),
                    Array.Empty<string>(),
                    Created: false);
            }

            throw;
        }
    }

    /// <summary>判断异常链中是否包含 SQLITE_BUSY(5)/SQLITE_LOCKED(6)。</summary>
    private static bool IsSqliteBusy(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 })
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// 按会话游标补拉：校验 1~100 项且不重复、0 ≤ lastSeq ≤ 会话 lastSeq、成员身份；
    /// 每会话取 seq &gt; lastSeq 升序最多 200 条，hasMore=任一会话取满 200 且有更多。
    /// </summary>
    public async Task<SyncPullResult> SyncAsync(
        string userId,
        SyncRequestFrame request,
        CancellationToken cancellationToken)
    {
        var cursors = request.Cursors ?? Array.Empty<SyncCursor>();
        if (cursors.Count is < 1 or > 100)
        {
            throw ApiException.ValidationFailed("cursors 数量应为 1~100 项");
        }

        var conversationIds = cursors.Select(cursor => cursor.ConversationId).ToList();
        if (conversationIds.Any(string.IsNullOrWhiteSpace) ||
            conversationIds.Distinct(StringComparer.Ordinal).Count() != conversationIds.Count)
        {
            throw ApiException.ValidationFailed("cursors 中 conversationId 不能为空且不得重复");
        }

        // 先统一校验成员/游标范围，避免部分会话返回、部分报错的半成品响应。
        var lastSeqByConversation = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var cursor in cursors)
        {
            var conversation = await EnsureMemberAsync(userId, cursor.ConversationId, cancellationToken);
            if (cursor.LastSeq < 0 || cursor.LastSeq > conversation.LastSeq)
            {
                throw ApiException.ValidationFailed(
                    $"lastSeq 超出范围（会话 {cursor.ConversationId} 当前 lastSeq={conversation.LastSeq}）");
            }

            lastSeqByConversation[cursor.ConversationId] = cursor.LastSeq;
        }

        var hasMore = false;
        var pages = new List<(string ConversationId, List<MessageEntity> Rows)>();
        foreach (var (conversationId, lastSeq) in lastSeqByConversation)
        {
            var rows = await _db.Messages.AsNoTracking()
                .Where(message => message.ConversationId == conversationId && message.Seq > lastSeq)
                .OrderBy(message => message.Seq)
                .Take(SyncBatchSize + 1)
                .ToListAsync(cancellationToken);

            if (rows.Count > SyncBatchSize)
            {
                rows.RemoveAt(rows.Count - 1);
                hasMore = true;
            }

            if (rows.Count > 0)
            {
                pages.Add((conversationId, rows));
            }
        }

        // F-09：本批所有会话的 replyTo 合并为一次批量查询，禁止逐条 N+1。
        var replies = await LoadRepliesAsync(pages.SelectMany(page => page.Rows), cancellationToken);
        var batches = pages
            .Select(page => new SyncBatch(
                page.ConversationId,
                page.Rows.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList()))
            .ToList();
        return new SyncPullResult(batches, hasMore);
    }

    /// <summary>
    /// REST 历史分页：beforeSeq 为空取最新一页；返回 messages 按 seq 升序与 hasMore（是否有更早消息）。
    /// </summary>
    public async Task<MessageHistoryPage> GetHistoryAsync(
        string userId,
        string conversationId,
        long? beforeSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        var id = ValidateId(conversationId, "conversationId");
        await EnsureMemberAsync(userId, id, cancellationToken);

        var pageSize = limit ?? HistoryDefaultLimit;
        if (pageSize is < 1 or > HistoryMaxLimit)
        {
            throw ApiException.ValidationFailed($"limit 应为 1~{HistoryMaxLimit}");
        }

        if (beforeSeq is <= 0)
        {
            throw ApiException.ValidationFailed("beforeSeq 必须为正整数");
        }

        var query = _db.Messages.AsNoTracking().Where(message => message.ConversationId == id);
        if (beforeSeq is not null)
        {
            query = query.Where(message => message.Seq < beforeSeq.Value);
        }

        var rows = await query
            .OrderByDescending(message => message.Seq)
            .Take(pageSize + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > pageSize;
        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        rows.Reverse();
        // F-09：历史分页同样先批量加载 replyTo，再逐条组装引用摘要（一次查询，避免 N+1）。
        var replies = await LoadRepliesAsync(rows, cancellationToken);
        return new MessageHistoryPage(
            rows.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList(),
            hasMore);
    }

    /// <summary>
    /// F-11 消息搜索（REST）：只搜本人所在会话、仅 text 且未删除，按 seq 降序返回。
    /// 实现分两层：先用 SQLite 的 json_extract 取出解码后的文本做 LIKE 粗筛（中文按字面、
    /// ASCII 大小写不敏感），再在内存中 OrdinalIgnoreCase 复核原文，纠正 %/_ 通配符造成的假命中；
    /// 复核后不足 limit 时用 beforeSeq 继续往后翻候选窗口（每轮 ≤200 条，最多 4 轮）。
    /// 注意：content_json 由 FrameCodec 序列化，非 ASCII 字符会被转义成 \uXXXX，因此不能直接
    /// LIKE 原始 JSON 列（会漏掉中文），必须经 json_extract(content_json, '$') 取回原文。
    /// 已知限制：LIKE 全表扫描，数据量大后需 FTS5；日志与异常中不打印 q 与消息内容。
    /// </summary>
    public async Task<MessageSearchPage> SearchAsync(
        string userId,
        string? query,
        string? conversationId,
        long? beforeSeq,
        int? limit,
        CancellationToken cancellationToken)
    {
        var keyword = (query ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            throw ApiException.ValidationFailed("搜索关键词 q 不能为空");
        }

        if (keyword.Length > SearchQueryMaxLength)
        {
            throw ApiException.ValidationFailed($"搜索关键词 q 不能超过 {SearchQueryMaxLength} 个字符");
        }

        var pageSize = limit ?? SearchDefaultLimit;
        if (pageSize is < 1 or > SearchMaxLimit)
        {
            throw ApiException.ValidationFailed($"limit 应为 1~{SearchMaxLimit}");
        }

        if (beforeSeq is <= 0)
        {
            throw ApiException.ValidationFailed("beforeSeq 必须为正整数");
        }

        string? scopeConversationId = null;
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            scopeConversationId = ValidateId(conversationId, "conversationId");
            await EnsureMemberAsync(userId, scopeConversationId, cancellationToken);
        }

        // LIKE 只做粗筛（%q%）；q 中的 % / _ 会被当通配符，内存复核会按字面 Contains 纠正误命中。
        var pattern = $"%{keyword}%";
        var collected = new List<MessageEntity>();
        long? cursor = beforeSeq;
        var hasMore = false;

        for (var round = 0; round < SearchMaxCandidateRounds && collected.Count < pageSize; round++)
        {
            var windowSize = Math.Min(pageSize * 4, SearchMaxCandidateWindow);
            var candidates = await SearchCandidateIdsAsync(
                userId,
                pattern,
                scopeConversationId,
                cursor,
                windowSize + 1,
                cancellationToken);

            hasMore = candidates.Count > windowSize;
            if (hasMore)
            {
                candidates.RemoveAt(candidates.Count - 1);
            }

            if (candidates.Count == 0)
            {
                break;
            }

            var ids = candidates.Select(candidate => candidate.Id).ToList();
            var rows = await _db.Messages.AsNoTracking()
                .Where(message => ids.Contains(message.Id))
                .ToListAsync(cancellationToken);
            var byId = rows.ToDictionary(message => message.Id, StringComparer.Ordinal);

            foreach (var candidate in candidates)
            {
                if (!byId.TryGetValue(candidate.Id, out var entity))
                {
                    continue;
                }

                var text = ExtractTextContent(entity.ContentJson);
                if (text is not null && text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    collected.Add(entity);
                    if (collected.Count >= pageSize)
                    {
                        // 窗口内可能仍有未复核的候选，保守返回 hasMore=true 供客户端继续翻页。
                        hasMore = true;
                        break;
                    }
                }
            }

            cursor = candidates[^1].Seq;
            if (!hasMore)
            {
                break;
            }
        }

        // F-09：搜索结果同样批量加载被引用消息，返回 reply 摘要（一次查询，避免 N+1）。
        var replies = await LoadRepliesAsync(collected, cancellationToken);
        return new MessageSearchPage(
            collected.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList(),
            hasMore);
    }

    /// <summary>从 content_json 中取出文本原文；非文本（对象/非法 JSON）返回 null。</summary>
    private static string? ExtractTextContent(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            return document.RootElement.ValueKind == JsonValueKind.String
                ? document.RootElement.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// LIKE 粗筛候选（按 seq 降序，最多 take 条）：json_extract 取出解码后的文本再匹配
    /// （content_json 里中文被转义为 \uXXXX，直接 LIKE JSON 列会漏掉中文），
    /// 同时限定当前用户所在会话；返回 (id, seq) 供上层复核与游标推进。
    /// </summary>
    private async Task<List<(string Id, long Seq)>> SearchCandidateIdsAsync(
        string userId,
        string pattern,
        string? conversationId,
        long? beforeSeq,
        int take,
        CancellationToken cancellationToken)
    {
        var connection = _db.Database.GetDbConnection();
        var shouldClose = connection.State != System.Data.ConnectionState.Open;
        if (shouldClose)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            // GetDbConnection().CreateCommand() 返回 DbCommand，这里转成 SqliteCommand 以使用
            // AddWithValue；连接由 EF Core 管理，打开/关闭按需处理。
            await using var command = (SqliteCommand)connection.CreateCommand();
            command.CommandText =
                """
                SELECT m.id, m.seq
                FROM messages AS m
                WHERE m.msg_type = 'text'
                  AND m.deleted_at IS NULL
                  AND json_extract(m.content_json, '$') LIKE @pattern
                  AND EXISTS (
                      SELECT 1 FROM conversation_members AS cm
                      WHERE cm.conversation_id = m.conversation_id AND cm.user_id = @userId)
                  AND (@conversationId IS NULL OR m.conversation_id = @conversationId)
                  AND (@beforeSeq IS NULL OR m.seq < @beforeSeq)
                ORDER BY m.seq DESC
                LIMIT @take
                """;
            command.Parameters.AddWithValue("@pattern", pattern);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@conversationId", (object?)conversationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@beforeSeq", (object?)beforeSeq ?? DBNull.Value);
            command.Parameters.AddWithValue("@take", take);

            var candidates = new List<(string Id, long Seq)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetInt64(1)));
            }

            return candidates;
        }
        finally
        {
            if (shouldClose)
            {
                await connection.CloseAsync();
            }
        }
    }

    /// <summary>
    /// 规范化后的发送内容：image/file 的 content 与 messages.file_id 以 files 记录为准（M3-03）。
    /// </summary>
    private sealed record NormalizedSendContent(MessageContent Content, string? FileId, MessageType MsgType);

    /// <summary>
    /// 校验并归一 content（M3-03）：text 原样返回；image/file 校验文件存在且属于发送者，
    /// 并用 files 记录重写 name/size/mime（客户端传入值不作为权威值）。
    /// </summary>
    private async Task<NormalizedSendContent> NormalizeContentAsync(
        MessageSendFrame request,
        string senderId,
        CancellationToken cancellationToken)
    {
        if (request.MsgType == MessageType.System)
        {
            throw ApiException.ValidationFailed("system 消息仅服务端生成，客户端不得发送");
        }

        if (request.Content.IsEmpty)
        {
            throw ApiException.ValidationFailed("content 不能为空");
        }

        switch (request.MsgType)
        {
            case MessageType.Text:
                if (!request.Content.IsString)
                {
                    throw ApiException.ValidationFailed("text 消息 content 必须是字符串");
                }

                var text = request.Content.GetString() ?? string.Empty;
                if (string.IsNullOrWhiteSpace(text))
                {
                    throw ApiException.ValidationFailed("文本内容不能为空");
                }

                if (text.Length > TextMaxLength)
                {
                    throw new ApiException(
                        ErrorCodes.MessageTooLarge,
                        $"文本内容超过 {TextMaxLength} 字符上限",
                        StatusCodes.Status400BadRequest);
                }

                return new NormalizedSendContent(request.Content, FileId: null, MessageType.Text);

            case MessageType.Image:
            case MessageType.File:
                if (!request.Content.IsObject)
                {
                    throw ApiException.ValidationFailed($"{request.MsgType.ToString().ToLowerInvariant()} 消息 content 必须是文件对象");
                }

                var content = request.Content.AsFileContent();
                if (content is null ||
                    string.IsNullOrWhiteSpace(content.FileId) ||
                    string.IsNullOrWhiteSpace(content.Name) ||
                    string.IsNullOrWhiteSpace(content.Mime))
                {
                    throw ApiException.ValidationFailed("文件对象字段不完整（fileId/name/size/mime）");
                }

                // size 字段必须存在且非负（结构校验）；真实大小以 files 记录为准，客户端传值不采信。
                if (content.Size < 0)
                {
                    throw ApiException.ValidationFailed("文件大小不能为负数");
                }

                var fileId = ValidateId(content.FileId, "fileId");
                var storedFile = await _db.Files.AsNoTracking()
                    .FirstOrDefaultAsync(file => file.Id == fileId, cancellationToken);
                if (storedFile is null ||
                    !string.Equals(storedFile.OwnerId, senderId, StringComparison.Ordinal))
                {
                    throw ApiException.ValidationFailed("文件不存在或无权使用该文件");
                }

                if (storedFile.Size > _appOptions.UploadMaxBytes)
                {
                    throw new ApiException(
                        ErrorCodes.MessageTooLarge,
                        $"文件大小超过上限（{_appOptions.UploadMaxBytes} 字节）",
                        StatusCodes.Status400BadRequest);
                }

                var normalizedFile = new FileContent(storedFile.Id, storedFile.OriginalName, storedFile.Size, storedFile.Mime);
                return new NormalizedSendContent(
                    MessageContent.FromObject(normalizedFile),
                    storedFile.Id,
                    request.MsgType);

            default:
                throw ApiException.ValidationFailed("不支持的 msgType");
        }
    }

    private static string ValidateId(string? value, string fieldName)
    {
        var trimmed = (value ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            throw ApiException.ValidationFailed($"{fieldName} 不能为空");
        }

        if (trimmed.Length > MaxIdLength)
        {
            throw ApiException.ValidationFailed($"{fieldName} 过长（最多 {MaxIdLength} 个字符）");
        }

        return trimmed;
    }

    /// <summary>校验会话存在且当前用户是成员；返回跟踪实体用于更新 last_message_id。</summary>
    private async Task<ConversationEntity> EnsureMemberAsync(
        string userId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        var isMember = await _db.ConversationMembers.AsNoTracking()
            .AnyAsync(
                member => member.ConversationId == conversationId && member.UserId == userId,
                cancellationToken);
        if (!isMember)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);
        }

        return conversation;
    }

    private static ProtocolMessage ToProtocolMessage(MessageEntity message) =>
        ConversationService.ToProtocolMessage(message);

    private static ProtocolMessage ToProtocolMessage(MessageEntity message, MessageReply? reply) =>
        ConversationService.ToProtocolMessage(message, reply);

    private static MessageReply? ToOptionalReply(MessageEntity? reply) =>
        reply is null ? null : ToReply(reply);

    /// <summary>按 replyTo 加载被引用消息（同会话、AsNoTracking）；不存在返回 null。</summary>
    private async Task<MessageEntity?> LoadReplyAsync(
        string? replyTo,
        string conversationId,
        CancellationToken cancellationToken)
    {
        if (replyTo is null)
        {
            return null;
        }

        return await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(
                message => message.Id == replyTo && message.ConversationId == conversationId,
                cancellationToken);
    }

    /// <summary>批量加载一页/一批消息引用的被引用消息（一次 IN 查询），返回 id → 实体字典。</summary>
    private async Task<Dictionary<string, MessageEntity>> LoadRepliesAsync(
        IEnumerable<MessageEntity> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows
            .Select(row => row.ReplyTo)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0)
        {
            return new Dictionary<string, MessageEntity>(StringComparer.Ordinal);
        }

        var replies = await _db.Messages.AsNoTracking()
            .Where(message => ids.Contains(message.Id))
            .ToListAsync(cancellationToken);
        return replies.ToDictionary(message => message.Id, StringComparer.Ordinal);
    }

    private static MessageReply? ReplyOf(MessageEntity row, IReadOnlyDictionary<string, MessageEntity> replies) =>
        row.ReplyTo is not null && replies.TryGetValue(row.ReplyTo, out var reply) ? ToReply(reply) : null;

    /// <summary>
    /// 被引用消息实体 → Reply 摘要（protocol §4.3）：text 前 120 字符（超出追加省略号）、
    /// system 取系统文案、image/file 带 fileId + name 且不返回 text。
    /// </summary>
    private static MessageReply ToReply(MessageEntity message)
    {
        using var document = JsonDocument.Parse(message.ContentJson);
        var root = document.RootElement;
        string? text = null;
        string? fileId = null;
        string? name = null;
        switch (message.MsgType)
        {
            case MessageType.Text:
                text = root.ValueKind == JsonValueKind.String ? root.GetString() : null;
                break;
            case MessageType.System:
                text = root.ValueKind == JsonValueKind.Object &&
                       root.TryGetProperty("text", out var textElement) &&
                       textElement.ValueKind == JsonValueKind.String
                    ? textElement.GetString()
                    : null;
                break;
            case MessageType.Image:
            case MessageType.File:
                fileId = message.FileId;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (fileId is null &&
                        root.TryGetProperty("fileId", out var fileIdElement) &&
                        fileIdElement.ValueKind == JsonValueKind.String)
                    {
                        fileId = fileIdElement.GetString();
                    }

                    if (root.TryGetProperty("name", out var nameElement) &&
                        nameElement.ValueKind == JsonValueKind.String)
                    {
                        name = nameElement.GetString();
                    }
                }

                break;
        }

        return new MessageReply(
            message.Id,
            message.Seq,
            message.SenderId,
            message.MsgType,
            TruncateReplyText(text),
            fileId,
            name);
    }

    /// <summary>按 Unicode 码点截断到 120 字符（不拆代理对），超出追加省略号。</summary>
    private static string? TruncateReplyText(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= ReplyTextMaxLength)
        {
            return text;
        }

        var index = 0;
        var count = 0;
        while (index < text.Length && count < ReplyTextMaxLength)
        {
            var isSurrogatePair = index + 1 < text.Length &&
                                  char.IsHighSurrogate(text[index]) &&
                                  char.IsLowSurrogate(text[index + 1]);
            index += isSurrogatePair ? 2 : 1;
            count++;
        }

        return string.Concat(text.AsSpan(0, index), "…");
    }
}
