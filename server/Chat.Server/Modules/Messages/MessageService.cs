using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Blocks;
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

/// <summary>message.recall 处理结果（F-18）：协议消息 + 会话成员（用于广播）+ 是否实际撤回（幂等重发为 false）。</summary>
public sealed record MessageRecallResult(
    ProtocolMessage Message,
    IReadOnlyList<string> MemberUserIds,
    bool Changed,
    string? PinnedClearedConversationId = null);

/// <summary>message.edit 处理结果（F-18）：协议消息 + 会话成员（用于广播）。</summary>
public sealed record MessageEditResult(ProtocolMessage Message, IReadOnlyList<string> MemberUserIds);

/// <summary>F-40：跨会话转发结果；created=false 表示 clientMsgId 幂等命中（不重复落库/广播）。</summary>
public sealed record MessageForwardResult(ProtocolMessage Message, IReadOnlyList<string> MemberUserIds, bool Created);

/// <summary>sync.request 补拉结果（reqId 由 Realtime 层回填）。</summary>
public sealed record SyncPullResult(IReadOnlyList<SyncBatch> Batches, bool HasMore);

/// <summary>REST 历史分页结果（与 apps/web/src/api/messages.ts 的 MessagePage 对齐）。</summary>
public sealed record MessageHistoryPage(IReadOnlyList<ProtocolMessage> Messages, bool HasMore);

/// <summary>
/// REST 消息搜索结果（F-11，不属于协议帧）：messages 按 seq 降序；hasMore 表示还能继续往后翻候选。
/// 响应形状与 apps/web/src/api/messages.ts 的 MessageSearchPage 对齐。
/// </summary>
public sealed record MessageSearchPage(
    IReadOnlyList<ProtocolMessage> Messages,
    bool HasMore,
    MessageSearchCursor? NextCursor = null);
/// <summary>F-36：按目标消息定位的上下文窗口（不推进同步游标）。</summary>
public sealed record MessageContextResult(IReadOnlyList<ProtocolMessage> Messages, bool HasOlder, bool HasNewer);

/// <summary>全局搜索翻页游标：按发送时间倒序、消息 ID 作为同时间排序条件（F-34）。</summary>
public sealed record MessageSearchCursor(long CreatedAt, string MessageId);

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

    /// <summary>语音消息大小上限（F-28，protocol §7）：5MB。</summary>
    public const int VoiceMaxBytes = 5 * 1024 * 1024;

    /// <summary>语音时长下限（毫秒，F-28）。</summary>
    public const int VoiceMinDurationMs = 1_000;

    /// <summary>语音时长上限（毫秒，F-28）。</summary>
    public const int VoiceMaxDurationMs = 60_000;

    /// <summary>语音文件 MIME（F-28）。</summary>
    private const string VoiceMime = "audio/webm";

    // F-11 消息搜索（REST，非协议帧）：q 长 1~50、limit 1~50（默认 20）。
    public const int SearchDefaultLimit = 20;
    public const int SearchMaxLimit = 50;
    public const int SearchQueryMaxLength = 50;

    /// <summary>最多扫描 4 个候选窗口（每窗口 min(limit*4, 200) 条），避免 LIKE 全表无限翻页。</summary>
    private const int SearchMaxCandidateRounds = 4;

    private const int SearchMaxCandidateWindow = 200;

    /// <summary>定位上下文：默认前后各 25 条，最多 50 条（F-36）。</summary>
    private const int MessageContextDefaultWindow = 25;

    private const int MessageContextMaxWindow = 50;

    private const int MaxIdLength = 64;

    /// <summary>Reply 摘要文本上限（protocol/protocol.md §4.3：前 120 字符，超出追加省略号）。</summary>
    public const int ReplyTextMaxLength = 120;

    private readonly ChatDbContext _db;
    private readonly BlockService _blocks;
    private readonly AppOptions _appOptions;

    public MessageService(ChatDbContext db, BlockService blocks, IOptions<AppOptions> appOptions)
    {
        _db = db;
        _blocks = blocks;
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

        // C1b：单聊任一方拉黑时拒绝新发送；上方幂等回执不拦（已落库消息的重试仍返回原回执）。
        if (conversation.Type == ConversationType.Direct)
        {
            await _blocks.EnsureDirectMessageAllowedAsync(senderId, conversationId, cancellationToken);
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

    /// <summary>
    /// 撤回消息（F-18）：仅发送者本人、窗口内、非 system；已撤回再撤回幂等（deletedAt 不变、不重复广播）。
    /// 撤回后置 deleted_at、content_json 置空文本（file_id 保留供孤儿清理引用，文件不立即删除）。
    /// </summary>
    public async Task<MessageRecallResult> RecallAsync(
        string userId,
        string conversationId,
        string messageId,
        CancellationToken cancellationToken)
    {
        var conversationIdValue = ValidateId(conversationId, "conversationId");
        var messageIdValue = ValidateId(messageId, "messageId");
        await EnsureMemberAsync(userId, conversationIdValue, cancellationToken);

        var message = await _db.Messages.FirstOrDefaultAsync(
            item => item.Id == messageIdValue && item.ConversationId == conversationIdValue,
            cancellationToken);
        if (message is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "消息不存在", StatusCodes.Status404NotFound);
        }

        if (!string.Equals(message.SenderId, userId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "只能撤回自己的消息", StatusCodes.Status403Forbidden);
        }

        if (message.MsgType == MessageType.System)
        {
            throw ApiException.ValidationFailed("系统消息不支持撤回");
        }

        var memberUserIds = await LoadMemberUserIdsAsync(conversationIdValue, cancellationToken);
        if (message.DeletedAt is not null)
        {
            // 幂等：已撤回不再改 deletedAt、不重复广播（protocol/protocol.md §4.3 规则 3）。
            return new MessageRecallResult(ToProtocolMessage(message), memberUserIds, Changed: false);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowSeconds = Math.Clamp(_appOptions.MessageRecallWindowSeconds, 1, 86400);
        if (now - message.CreatedAt > windowSeconds * 1000L)
        {
            throw ApiException.ValidationFailed("超过可撤回时间");
        }

        message.DeletedAt = now;
        // 撤回后不再暴露原文与文件信息（协议 §4.3 规则 4）；file_id 保留供孤儿清理引用。
        message.ContentJson = "\"\"";
        // F-36：撤回的是群置顶消息时清空置顶引用，由 MessageHub 在 message.recalled 之后推送 conv.updated。
        var pinnedClearedConversationId = await ClearPinnedMessageIfMatchesAsync(message, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        return new MessageRecallResult(ToProtocolMessage(message), memberUserIds, Changed: true, pinnedClearedConversationId);
    }

    /// <summary>
    /// 编辑消息（F-18）：仅发送者本人、仅 text、未撤回、窗口内；content trim 后按 Unicode 码点 1..4096。
    /// 每次编辑刷新 edited_at；seq / clientMsgId 幂等语义不变。返回最新 Message 与成员 ID。
    /// </summary>
    public async Task<MessageEditResult> EditAsync(
        string userId,
        string conversationId,
        string messageId,
        string? content,
        CancellationToken cancellationToken)
    {
        var conversationIdValue = ValidateId(conversationId, "conversationId");
        var messageIdValue = ValidateId(messageId, "messageId");
        await EnsureMemberAsync(userId, conversationIdValue, cancellationToken);

        var message = await _db.Messages.FirstOrDefaultAsync(
            item => item.Id == messageIdValue && item.ConversationId == conversationIdValue,
            cancellationToken);
        if (message is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "消息不存在", StatusCodes.Status404NotFound);
        }

        if (!string.Equals(message.SenderId, userId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "只能编辑自己的消息", StatusCodes.Status403Forbidden);
        }

        if (message.MsgType != MessageType.Text)
        {
            throw ApiException.ValidationFailed("仅文本消息支持编辑");
        }

        if (message.DeletedAt is not null)
        {
            throw ApiException.ValidationFailed("已撤回的消息不支持编辑");
        }

        var trimmed = (content ?? string.Empty).Trim();
        var length = trimmed.EnumerateRunes().Count();
        if (length == 0)
        {
            throw ApiException.ValidationFailed("编辑内容不能为空");
        }

        if (length > TextMaxLength)
        {
            throw ApiException.ValidationFailed($"文本内容超过 {TextMaxLength} 字符上限");
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var windowSeconds = Math.Clamp(_appOptions.MessageEditWindowSeconds, 1, 86400);
        if (now - message.CreatedAt > windowSeconds * 1000L)
        {
            throw ApiException.ValidationFailed("超过可编辑时间");
        }

        message.ContentJson = JsonSerializer.Serialize(trimmed, FrameCodec.JsonOptions);
        message.EditedAt = now;
        await _db.SaveChangesAsync(cancellationToken);

        var memberUserIds = await LoadMemberUserIdsAsync(conversationIdValue, cancellationToken);
        return new MessageEditResult(ToProtocolMessage(message), memberUserIds);
    }

    /// <summary>
    /// F-40：把源消息转发到目标会话（text/image/file/voice）。
    /// 转发者必须仍是源会话成员、仍属于目标会话；源消息未撤回且非 system；附件消息的 files 记录必须仍存在。
    /// 不复制文件、不要求客户端重新上传：新消息复用同一 files.id（下载鉴权按目标消息所在会话判定）。
    /// 新消息使用调用方提供的稳定 clientMsgId 走普通 (conversation_id, client_msg_id) 幂等。
    /// 不支持把 system 消息、已撤回消息转发，也不复制 replyTo/editedAt。
    /// </summary>
    public async Task<MessageForwardResult> ForwardAsync(
        string userId,
        string? messageId,
        string? targetConversationId,
        string? clientMsgId,
        CancellationToken cancellationToken)
    {
        var messageIdValue = ValidateId(messageId, "messageId");
        var targetId = ValidateId(targetConversationId, "targetConversationId");
        var clientId = ValidateId(clientMsgId, "clientMsgId");

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        // F-40：成员资格、撤回状态、附件可用性校验与 seq 分配/插入必须在同一事务内完成。
        // 否则校验后、写入前发生退群或撤回时，仍可能把旧内容写进目标会话。
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // 目标会话成员校验先于幂等查询：重试仍需确认转发者仍有目标会话访问权。
            var targetConversation = await EnsureMemberAsync(userId, targetId, cancellationToken);

            var existing = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(
                item => item.ConversationId == targetId && item.ClientMsgId == clientId,
                cancellationToken);
            if (existing is not null)
            {
                return new MessageForwardResult(ToProtocolMessage(existing), Array.Empty<string>(), Created: false);
            }

            // C1b：转发到单聊时同样校验拉黑关系（幂等回执已在上方返回，不拦重复请求）。
            if (targetConversation.Type == ConversationType.Direct)
            {
                await _blocks.EnsureDirectMessageAllowedAsync(userId, targetId, cancellationToken);
            }

            var source = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(item => item.Id == messageIdValue, cancellationToken);
            if (source is null)
            {
                throw ApiException.NotFound("原消息不存在");
            }

            var stillMember = await _db.ConversationMembers.AsNoTracking().AnyAsync(
                member => member.ConversationId == source.ConversationId && member.UserId == userId,
                cancellationToken);
            if (!stillMember)
            {
                throw ApiException.Forbidden("无权访问原消息");
            }

            if (source.DeletedAt is not null)
            {
                throw ApiException.ValidationFailed("已撤回的消息不能转发");
            }

            if (source.MsgType == MessageType.System)
            {
                throw ApiException.ValidationFailed("系统消息不能转发");
            }

            if (source.FileId is not null)
            {
                var fileExists = await _db.Files.AsNoTracking()
                    .AnyAsync(file => file.Id == source.FileId, cancellationToken);
                if (!fileExists)
                {
                    throw ApiException.ValidationFailed("原消息的附件已不可用");
                }
            }

            var seq = await SeqAllocator.AllocateAsync(_db, targetId, cancellationToken);
            var entity = new MessageEntity
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = targetId,
                SenderId = userId,
                ClientMsgId = clientId,
                Seq = seq,
                MsgType = source.MsgType,
                // 内容快照直接沿用原消息（编辑后的文本为最新版本；附件仍在原文件记录中）。
                ContentJson = source.ContentJson,
                FileId = source.FileId,
                ReplyTo = null,
                CreatedAt = now,
            };

            _db.Messages.Add(entity);
            targetConversation.LastMessageId = entity.Id;
            await _db.SaveChangesAsync(cancellationToken);

            var memberUserIds = await _db.ConversationMembers.AsNoTracking()
                .Where(member => member.ConversationId == targetId)
                .Select(member => member.UserId)
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);
            return new MessageForwardResult(ToProtocolMessage(entity), memberUserIds, Created: true);
        }
        catch (DbUpdateException)
        {
            // 并发重试：唯一索引 (conversation_id, client_msg_id) 兜底，回滚后返回已存在的转发消息。
            await transaction.RollbackAsync(CancellationToken.None);
            var raced = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(
                    item => item.ConversationId == targetId && item.ClientMsgId == clientId,
                    cancellationToken);
            if (raced is not null)
            {
                return new MessageForwardResult(ToProtocolMessage(raced), Array.Empty<string>(), Created: false);
            }

            throw;
        }
    }

    /// <summary>读取会话全部成员用户 ID（撤回/编辑广播用）。</summary>
    /// <summary>F-36：若撤回的是群置顶消息，清空 conversations.pinned_message_id 并返回会话 ID。</summary>
    private async Task<string?> ClearPinnedMessageIfMatchesAsync(
        MessageEntity message,
        CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(row => row.Id == message.ConversationId, cancellationToken);
        if (conversation is null ||
            !string.Equals(conversation.PinnedMessageId, message.Id, StringComparison.Ordinal))
        {
            return null;
        }

        conversation.PinnedMessageId = null;
        return conversation.Id;
    }

    private async Task<IReadOnlyList<string>> LoadMemberUserIdsAsync(
        string conversationId,
        CancellationToken cancellationToken) =>
        await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);

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

            if (cursor.UpdatedAfter is < 0)
            {
                throw ApiException.ValidationFailed(
                    $"updatedAfter 不能为负数（会话 {cursor.ConversationId}）");
            }

            lastSeqByConversation[cursor.ConversationId] = cursor.LastSeq;
        }

        // 本次响应统一使用同一个服务端时间作为 updatedThrough 上界，保证下次请求不重不漏。
        // 回退 1ms：与 cutoff 同毫秒内提交的编辑/撤回会在下次（> cutoff）被返回，
        // 宁可重复一次（客户端按 messageId 幂等覆盖）也不丢状态变化。
        var syncCutoff = Math.Max(1, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 1);
        var hasMore = false;
        var pages = new List<SyncPage>();
        foreach (var cursor in cursors)
        {
            var conversationId = cursor.ConversationId;
            var lastSeq = lastSeqByConversation[conversationId];
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

            // F-34：补齐断线期间对「已缓存消息」的撤回/编辑；seq 不变，只带最新状态。
            // 状态变化数量远小于消息量，随批一次性返回（messages 仍按每批 200 条分页）。
            var updates = new List<MessageEntity>();
            long? updatedThrough = null;
            if (cursor.UpdatedAfter is > 0)
            {
                updatedThrough = syncCutoff;
                updates = await _db.Messages.AsNoTracking()
                    .Where(message => message.ConversationId == conversationId
                        && ((message.EditedAt != null && message.EditedAt > cursor.UpdatedAfter)
                            || (message.DeletedAt != null && message.DeletedAt > cursor.UpdatedAfter)))
                    .OrderBy(message => message.EditedAt ?? message.DeletedAt ?? 0)
                    .ThenBy(message => message.Seq)
                    .ToListAsync(cancellationToken);
            }

            if (rows.Count > 0 || updates.Count > 0)
            {
                pages.Add(new SyncPage(conversationId, rows, updates, updatedThrough));
            }
        }

        // F-09：本批所有会话（含状态变化消息）的 replyTo 合并为一次批量查询，禁止逐条 N+1。
        var replies = await LoadRepliesAsync(
            pages.SelectMany(page => page.Rows).Concat(pages.SelectMany(page => page.Updates)),
            cancellationToken);
        var batches = pages
            .Select(page => new SyncBatch(
                page.ConversationId,
                page.Rows.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList(),
                page.Updates.Count == 0
                    ? null
                    : page.Updates.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList(),
                page.UpdatedThrough))
            .ToList();
        return new SyncPullResult(batches, hasMore);
    }

    /// <summary>sync 单会话结果：新消息 + 状态变化。</summary>
    private sealed record SyncPage(
        string ConversationId,
        List<MessageEntity> Rows,
        List<MessageEntity> Updates,
        long? UpdatedThrough);

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
    /// F-36：按 messageId 或 seq 一次性加载目标消息附近窗口，用于置顶/搜索定位超出缓存范围的旧消息。
    /// 只读取历史，不推进 lastSeq / 同步水位；窗口默认前后各 25 条（最大 50）。
    /// </summary>
    public async Task<MessageContextResult> GetMessageContextAsync(
        string userId,
        string conversationId,
        string? messageId,
        long? seq,
        int? before,
        int? after,
        CancellationToken cancellationToken)
    {
        var scopeId = ValidateId(conversationId, "conversationId");
        await EnsureMemberAsync(userId, scopeId, cancellationToken);

        var hasMessageId = !string.IsNullOrWhiteSpace(messageId);
        if (hasMessageId == (seq is not null))
        {
            throw ApiException.ValidationFailed("messageId 与 seq 必须且只能提供一个");
        }

        MessageEntity? target;
        if (hasMessageId)
        {
            var id = ValidateId(messageId!, "messageId");
            target = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == scopeId && m.Id == id, cancellationToken);
        }
        else
        {
            if (seq is <= 0)
            {
                throw ApiException.ValidationFailed("seq 必须为正整数");
            }

            target = await _db.Messages.AsNoTracking()
                .FirstOrDefaultAsync(m => m.ConversationId == scopeId && m.Seq == seq, cancellationToken);
        }

        if (target is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "消息不存在", 404);
        }

        var beforeCount = Math.Clamp(before ?? MessageContextDefaultWindow, 0, MessageContextMaxWindow);
        var afterCount = Math.Clamp(after ?? MessageContextDefaultWindow, 0, MessageContextMaxWindow);

        var older = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == scopeId && m.Seq <= target.Seq)
            .OrderByDescending(m => m.Seq)
            .Take(beforeCount + 1)
            .ToListAsync(cancellationToken);
        var newer = await _db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == scopeId && m.Seq > target.Seq)
            .OrderBy(m => m.Seq)
            .Take(afterCount)
            .ToListAsync(cancellationToken);

        older.Reverse();
        var window = older.Concat(newer).ToList();
        var minSeq = window[0].Seq;
        var maxSeq = window[^1].Seq;
        var hasOlder = await _db.Messages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == scopeId && m.Seq < minSeq, cancellationToken);
        var hasNewer = await _db.Messages.AsNoTracking()
            .AnyAsync(m => m.ConversationId == scopeId && m.Seq > maxSeq, cancellationToken);

        var replies = await LoadRepliesAsync(window, cancellationToken);
        return new MessageContextResult(
            window.Select(m => ToProtocolMessage(m, ReplyOf(m, replies))).ToList(),
            hasOlder,
            hasNewer);
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
        CancellationToken cancellationToken,
        long? beforeCreatedAt = null,
        string? beforeMessageId = null,
        string? senderId = null,
        long? from = null,
        long? to = null,
        bool timeCursor = false,
        bool linkOnly = false)
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

        if (from is < 0 || to is < 0)
        {
            throw ApiException.ValidationFailed("from/to 必须为非负毫秒时间戳");
        }

        if (from is not null && to is not null && from > to)
        {
            throw ApiException.ValidationFailed("起始时间不能晚于结束时间");
        }

        string? scopeConversationId = null;
        string? senderUserId = null;
        if (!string.IsNullOrWhiteSpace(senderId))
        {
            senderUserId = ValidateId(senderId, "senderId");
        }

        MessageSearchCursor? cursor = null;
        var timeCursorMode = false;
        if (!string.IsNullOrWhiteSpace(conversationId))
        {
            scopeConversationId = ValidateId(conversationId, "conversationId");
            await EnsureMemberAsync(userId, scopeConversationId, cancellationToken);
            if (beforeCreatedAt is not null || beforeMessageId is not null || timeCursor)
            {
                throw ApiException.ValidationFailed("会话内搜索只支持 beforeSeq 翻页");
            }
        }
        else if (timeCursor)
        {
            // 新客户端全局搜索：显式启用发送时间游标（F-36），按 (created_at, id) 排序翻页。
            timeCursorMode = true;
            if (beforeSeq is not null)
            {
                throw ApiException.ValidationFailed("时间游标模式不支持 beforeSeq");
            }

            if ((beforeCreatedAt is null) != string.IsNullOrWhiteSpace(beforeMessageId))
            {
                throw ApiException.ValidationFailed("beforeCreatedAt 与 beforeMessageId 必须同时提供");
            }

            if (beforeCreatedAt is not null)
            {
                cursor = new MessageSearchCursor(beforeCreatedAt.Value, beforeMessageId!);
            }
        }
        else if (beforeCreatedAt is not null || beforeMessageId is not null)
        {
            throw ApiException.ValidationFailed("全局时间游标需显式传 timeCursor=true");
        }

        // LIKE 只做粗筛（%q%）；q 中的 % / _ 会被当通配符，内存复核会按字面 Contains 纠正误命中。
        // 普通模式匹配正文/文件名；显式链接模式（linkOnly）只匹配识别出的 HTTP/HTTPS URL。
        var pattern = $"%{keyword}%";
        var collected = new List<(MessageEntity Entity, (string Id, long Seq, long CreatedAt) Candidate)>();
        long? seqCursor = beforeSeq;
        var hasMore = false;

        while (true)
        {
            var windowSize = Math.Min(pageSize * 4, SearchMaxCandidateWindow);
            var candidates = await SearchCandidateIdsAsync(
                userId,
                pattern,
                scopeConversationId,
                seqCursor,
                timeCursorMode ? cursor : null,
                legacyGlobalSeq: scopeConversationId is null && !timeCursorMode,
                senderUserId,
                from,
                to,
                windowSize + 1,
                cancellationToken);

            var windowTruncated = candidates.Count > windowSize;
            if (windowTruncated)
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

                if (!MessageMatchesSearch(entity, keyword, linkOnly))
                {
                    continue;
                }

                collected.Add((entity, candidate));
                if (collected.Count > pageSize)
                {
                    // 多读一条实际命中：确认还有下一页，同时保证游标仍指向本页最后一条。
                    hasMore = true;
                    break;
                }
            }

            if (hasMore)
            {
                break;
            }

            // 游标只推进到「已实际复核过」的候选，避免跳过同窗口内未复核的命中。
            var lastCandidate = candidates[^1];
            if (timeCursorMode)
            {
                cursor = new MessageSearchCursor(lastCandidate.CreatedAt, lastCandidate.Id);
            }
            else
            {
                seqCursor = lastCandidate.Seq;
            }

            if (!windowTruncated)
            {
                break;
            }
        }

        if (collected.Count > pageSize)
        {
            collected.RemoveRange(pageSize, collected.Count - pageSize);
        }

        MessageSearchCursor? nextCursor = null;
        if (timeCursorMode && hasMore && collected.Count > 0)
        {
            var last = collected[^1].Candidate;
            nextCursor = new MessageSearchCursor(last.CreatedAt, last.Id);
        }

        // F-09：搜索结果同样批量加载被引用消息，返回 reply 摘要（一次查询，避免 N+1）。
        var messages = collected.Select(item => item.Entity).ToList();
        var replies = await LoadRepliesAsync(messages, cancellationToken);
        return new MessageSearchPage(
            messages.Select(row => ToProtocolMessage(row, ReplyOf(row, replies))).ToList(),
            hasMore,
            nextCursor);
    }

    /// <summary>HTTP/HTTPS URL 识别（链接搜索只在这些 URL 内匹配关键词）。</summary>
    private static readonly Regex HttpUrlPattern = new(
        @"https?://[^\s<>`）】]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>从正文中提取 HTTP/HTTPS URL，并去掉常见的尾部标点。</summary>
    private static IEnumerable<string> ExtractHttpUrls(string text)
    {
        foreach (Match match in HttpUrlPattern.Matches(text))
        {
            var url = match.Value.TrimEnd('.', ',', ';', ':', '!', '?', ')', ']', '}', '。', '，', '；', '：', '！', '？', '）', '】');
            if (url.Length > "https://".Length)
            {
                yield return url;
            }
        }
    }

    /// <summary>
    /// F-37：搜索命中复核——普通模式匹配 text 正文（含正文里的链接字样）或 image/file 文件名；
    /// 显式链接模式（linkOnly）只匹配识别出的 HTTP/HTTPS URL，不匹配普通正文或文件名。
    /// </summary>
    private static bool MessageMatchesSearch(MessageEntity message, string keyword, bool linkOnly)
    {
        if (linkOnly)
        {
            if (message.MsgType != MessageType.Text)
            {
                return false;
            }

            var linkText = ExtractTextContent(message.ContentJson);
            return linkText is not null &&
                ExtractHttpUrls(linkText).Any(url => url.Contains(keyword, StringComparison.OrdinalIgnoreCase));
        }

        if (message.MsgType == MessageType.Text)
        {
            var text = ExtractTextContent(message.ContentJson);
            return text is not null && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        if (message.MsgType is MessageType.Image or MessageType.File)
        {
            var name = ExtractContentName(message.ContentJson);
            return name is not null && name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    /// <summary>从 content_json 的对象里取 name（文件消息文件名）；非对象/缺字段返回 null。</summary>
    private static string? ExtractContentName(string contentJson)
    {
        try
        {
            using var document = JsonDocument.Parse(contentJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            return document.RootElement.TryGetProperty("name", out var name) &&
                   name.ValueKind == JsonValueKind.String
                ? name.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
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
    /// LIKE 粗筛候选：text 匹配正文（链接同样在正文中），image/file 匹配文件名；
    /// 限定当前用户所在会话并排除已撤回。会话内按 seq 倒序；全局新客户端按 (created_at, id)
    /// 倒序，旧客户端只传 beforeSeq 时保持 seq 游标兼容（F-36）。
    /// </summary>
    private async Task<List<(string Id, long Seq, long CreatedAt)>> SearchCandidateIdsAsync(
        string userId,
        string pattern,
        string? conversationId,
        long? beforeSeq,
        MessageSearchCursor? cursor,
        bool legacyGlobalSeq,
        string? senderId,
        long? from,
        long? to,
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
            // 会话内：按 seq 倒序、beforeSeq 游标（沿用 F-11 行为）；
            // 全局：seq 只在单会话内可比，改用 (created_at, id) 复合游标，保证多会话翻页不重不漏。
            var isGlobal = conversationId is null;
            await using var command = (SqliteCommand)connection.CreateCommand();
            command.CommandText = legacyGlobalSeq
                ? """
                  SELECT m.id, m.seq, m.created_at
                  FROM messages AS m
                  WHERE m.msg_type IN ('text', 'image', 'file')
                    AND m.deleted_at IS NULL
                    AND (json_extract(m.content_json, '$') LIKE @pattern
                         OR json_extract(m.content_json, '$.name') LIKE @pattern)
                    AND (@senderId IS NULL OR m.sender_id = @senderId)
                    AND (@from IS NULL OR m.created_at >= @from)
                    AND (@to IS NULL OR m.created_at <= @to)
                    AND EXISTS (
                        SELECT 1 FROM conversation_members AS cm
                        WHERE cm.conversation_id = m.conversation_id AND cm.user_id = @userId)
                    AND (@beforeSeq IS NULL OR m.seq < @beforeSeq)
                  ORDER BY m.seq DESC, m.created_at DESC, m.id DESC
                  LIMIT @take
                  """
                : isGlobal
                ? """
                  SELECT m.id, m.seq, m.created_at
                  FROM messages AS m
                  WHERE m.msg_type IN ('text', 'image', 'file')
                    AND m.deleted_at IS NULL
                    AND (json_extract(m.content_json, '$') LIKE @pattern
                         OR json_extract(m.content_json, '$.name') LIKE @pattern)
                    AND (@senderId IS NULL OR m.sender_id = @senderId)
                    AND (@from IS NULL OR m.created_at >= @from)
                    AND (@to IS NULL OR m.created_at <= @to)
                    AND EXISTS (
                        SELECT 1 FROM conversation_members AS cm
                        WHERE cm.conversation_id = m.conversation_id AND cm.user_id = @userId)
                    AND (@beforeCreatedAt IS NULL
                         OR m.created_at < @beforeCreatedAt
                         OR (m.created_at = @beforeCreatedAt AND m.id < @beforeMessageId))
                  ORDER BY m.created_at DESC, m.id DESC
                  LIMIT @take
                  """
                : """
                  SELECT m.id, m.seq, m.created_at
                  FROM messages AS m
                  WHERE m.msg_type IN ('text', 'image', 'file')
                    AND m.deleted_at IS NULL
                    AND (json_extract(m.content_json, '$') LIKE @pattern
                         OR json_extract(m.content_json, '$.name') LIKE @pattern)
                    AND (@senderId IS NULL OR m.sender_id = @senderId)
                    AND (@from IS NULL OR m.created_at >= @from)
                    AND (@to IS NULL OR m.created_at <= @to)
                    AND EXISTS (
                        SELECT 1 FROM conversation_members AS cm
                        WHERE cm.conversation_id = m.conversation_id AND cm.user_id = @userId)
                    AND m.conversation_id = @conversationId
                    AND (@beforeSeq IS NULL OR m.seq < @beforeSeq)
                  ORDER BY m.seq DESC
                  LIMIT @take
                  """;
            command.Parameters.AddWithValue("@pattern", pattern);
            command.Parameters.AddWithValue("@userId", userId);
            command.Parameters.AddWithValue("@take", take);
            command.Parameters.AddWithValue("@conversationId", (object?)conversationId ?? DBNull.Value);
            command.Parameters.AddWithValue("@beforeSeq", (object?)beforeSeq ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@beforeCreatedAt",
                (object?)cursor?.CreatedAt ?? DBNull.Value);
            command.Parameters.AddWithValue(
                "@beforeMessageId",
                (object?)cursor?.MessageId ?? DBNull.Value);
            command.Parameters.AddWithValue("@senderId", (object?)senderId ?? DBNull.Value);
            command.Parameters.AddWithValue("@from", (object?)from ?? DBNull.Value);
            command.Parameters.AddWithValue("@to", (object?)to ?? DBNull.Value);

            var candidates = new List<(string Id, long Seq, long CreatedAt)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                candidates.Add((reader.GetString(0), reader.GetInt64(1), reader.GetInt64(2)));
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

            case MessageType.Voice:
                if (!request.Content.IsObject)
                {
                    throw ApiException.ValidationFailed("voice 消息 content 必须是语音对象");
                }

                var voice = request.Content.As<VoiceContent>();
                if (voice is null || string.IsNullOrWhiteSpace(voice.FileId))
                {
                    throw ApiException.ValidationFailed("语音对象字段不完整（fileId/durationMs）");
                }

                var voiceFileId = ValidateId(voice.FileId, "fileId");
                var voiceFile = await _db.Files.AsNoTracking()
                    .FirstOrDefaultAsync(file => file.Id == voiceFileId, cancellationToken);
                if (voiceFile is null ||
                    !string.Equals(voiceFile.OwnerId, senderId, StringComparison.Ordinal))
                {
                    throw ApiException.ValidationFailed("文件不存在或无权使用该文件");
                }

                if (!string.Equals(voiceFile.Mime, VoiceMime, StringComparison.OrdinalIgnoreCase))
                {
                    throw ApiException.ValidationFailed("语音消息必须引用音频文件（audio/webm）");
                }

                if (voiceFile.Size > VoiceMaxBytes)
                {
                    throw new ApiException(
                        ErrorCodes.MessageTooLarge,
                        $"语音文件超过上限（最大 {VoiceMaxBytes / 1024 / 1024}MB）",
                        StatusCodes.Status400BadRequest);
                }

                if (voice.DurationMs < VoiceMinDurationMs || voice.DurationMs > VoiceMaxDurationMs)
                {
                    throw ApiException.ValidationFailed(
                        $"语音时长超出范围（{VoiceMinDurationMs}~{VoiceMaxDurationMs} 毫秒）");
                }

                // 时长由客户端上报，服务端只做范围校验；文件 ID 以 files 记录为准，落 messages.file_id。
                return new NormalizedSendContent(
                    MessageContent.FromObject(new VoiceContent(voiceFile.Id, voice.DurationMs)),
                    voiceFile.Id,
                    MessageType.Voice);

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
        // F-18：被引用消息已撤回时固定「[已撤回]」，messageId 保留供点击定位（协议 §4.3 规则 8）。
        if (message.DeletedAt is not null)
        {
            return new MessageReply(message.Id, message.Seq, message.SenderId, message.MsgType, "[已撤回]", null, null);
        }

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
            case MessageType.Voice:
                // 语音不返回 fileId/name，固定摘要（protocol §4.3 Reply 对象，F-28）。
                text = "[语音]";
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
