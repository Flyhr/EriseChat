using System.Data.Common;
using System.Globalization;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Conversations;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
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
/// 消息核心业务（M1-08，计划 §4.3/§4.4 + AGENTS §5）：
/// 1) message.send：成员校验 → 事务内分配严格 +1 seq → 落库（唯一约束幂等）→ 更新 last_message_id；
/// 2) sync.request：按游标补拉，每会话每批 ≤200 条、按 seq 升序；
/// 3) REST 历史分页：beforeSeq + limit（默认 50、上限 200），按 seq 升序返回。
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

    private const int MaxIdLength = 64;

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
        ValidateContent(request);
        var replyTo = string.IsNullOrWhiteSpace(request.ReplyTo) ? null : ValidateId(request.ReplyTo, "replyTo");

        // SQLite 默认延迟事务在并发写入时可能因锁升级冲突返回 SQLITE_BUSY / SQLITE_LOCKED（5/6），
        // busy_timeout 只覆盖等待写锁的场景；此处回滚后整体重试，保证多连接并发发送仍分配到唯一 seq。
        const int maxAttempts = 5;
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await SendOnceAsync(senderId, conversationId, clientMsgId, replyTo, request, cancellationToken);
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
        CancellationToken cancellationToken)
    {
        var conversation = await EnsureMemberAsync(senderId, conversationId, cancellationToken);

        if (replyTo is not null)
        {
            var replyExists = await _db.Messages.AsNoTracking()
                .AnyAsync(message => message.Id == replyTo && message.ConversationId == conversationId, cancellationToken);
            if (!replyExists)
            {
                throw ApiException.ValidationFailed("replyTo 指向的消息不存在或不属于该会话");
            }
        }

        // 幂等短路：同一会话内同一 clientMsgId 直接回执，不重复落库（协议 §4.3 规则 1）。
        var existing = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(
                message => message.ConversationId == conversationId && message.ClientMsgId == clientMsgId,
                cancellationToken);
        if (existing is not null)
        {
            return new MessageSendResult(ToProtocolMessage(existing), Array.Empty<string>(), Created: false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            var seq = await AllocateSeqAsync(conversationId, cancellationToken);
            var entity = new MessageEntity
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = conversationId,
                SenderId = senderId,
                ClientMsgId = clientMsgId,
                Seq = seq,
                MsgType = request.MsgType,
                ContentJson = request.Content.Raw.GetRawText(),
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
            return new MessageSendResult(ToProtocolMessage(entity), memberUserIds, Created: true);
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
                return new MessageSendResult(ToProtocolMessage(raced), Array.Empty<string>(), Created: false);
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

        var batches = new List<SyncBatch>();
        var hasMore = false;
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
                batches.Add(new SyncBatch(conversationId, rows.Select(ToProtocolMessage).ToList()));
            }
        }

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
        return new MessageHistoryPage(rows.Select(ToProtocolMessage).ToList(), hasMore);
    }

    /// <summary>事务内分配会话内严格 +1 的 seq（protocol/protocol.md §4.3、AGENTS §5.1）。</summary>
    private async Task<long> AllocateSeqAsync(string conversationId, CancellationToken cancellationToken)
    {
        var dbConnection = _db.Database.GetDbConnection();
        await using var command = dbConnection.CreateCommand();
        command.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "UPDATE conversations SET last_seq = last_seq + 1 WHERE id = @id RETURNING last_seq;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            throw new ApiException(ErrorCodes.InternalError, "会话序号分配失败", StatusCodes.Status500InternalServerError);
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }

    private void ValidateContent(MessageSendFrame request)
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

                break;

            case MessageType.Image:
            case MessageType.File:
                if (!request.Content.IsObject)
                {
                    throw ApiException.ValidationFailed($"{request.MsgType.ToString().ToLowerInvariant()} 消息 content 必须是文件对象");
                }

                var file = request.Content.AsFileContent();
                if (file is null ||
                    string.IsNullOrWhiteSpace(file.FileId) ||
                    string.IsNullOrWhiteSpace(file.Name) ||
                    string.IsNullOrWhiteSpace(file.Mime))
                {
                    throw ApiException.ValidationFailed("文件对象字段不完整（fileId/name/size/mime）");
                }

                if (file.Size < 0)
                {
                    throw ApiException.ValidationFailed("文件大小不能为负数");
                }

                if (file.Size > _appOptions.UploadMaxBytes)
                {
                    throw new ApiException(
                        ErrorCodes.MessageTooLarge,
                        $"文件大小超过上限（{_appOptions.UploadMaxBytes} 字节）",
                        StatusCodes.Status400BadRequest);
                }

                break;

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
}
