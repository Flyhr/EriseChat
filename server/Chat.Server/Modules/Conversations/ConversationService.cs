using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using ConversationMemberEntity = Chat.Server.Data.Entities.ConversationMember;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Modules.Conversations;

/// <summary>
/// 会话业务（M1-07 最小集）：创建单聊（direct_key 去重）、当前用户会话列表、单会话读取（成员校验）。
/// 输出 DTO 为 protocol/protocol.md §4.5 的 Conversation 对象，REST 与 WS conv.list 共用。
/// </summary>
public sealed class ConversationService
{
    private const int TitleMaxLength = 100;

    private readonly ChatDbContext _db;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(ChatDbContext db, ILogger<ConversationService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>当前用户全部会话（按最新消息时间/创建时间倒序），含成员与我的已读游标。</summary>
    public async Task<IReadOnlyList<Conversation>> ListAsync(string currentUserId, CancellationToken cancellationToken)
    {
        var conversationIds = await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.UserId == currentUserId)
            .Select(member => member.ConversationId)
            .ToListAsync(cancellationToken);

        if (conversationIds.Count == 0)
        {
            return Array.Empty<Conversation>();
        }

        var conversations = await _db.Conversations.AsNoTracking()
            .Where(conversation => conversationIds.Contains(conversation.Id))
            .ToListAsync(cancellationToken);

        return await BuildAsync(conversations, currentUserId, cancellationToken);
    }

    /// <summary>
    /// 创建单聊：当前用户 + memberIds 中唯一对方；direct_key 由两个 UUID 升序拼接并受唯一索引保护，
    /// 已存在（含并发创建撞唯一索引）时返回原会话，保证幂等。
    /// </summary>
    public async Task<Conversation> CreateDirectAsync(
        string currentUserId,
        string? title,
        IReadOnlyList<string>? memberIds,
        CancellationToken cancellationToken)
    {
        if (memberIds is null || memberIds.Count != 1)
        {
            throw ApiException.ValidationFailed("单聊的 memberIds 必须恰好包含一个对方用户 ID");
        }

        var otherUserId = (memberIds[0] ?? string.Empty).Trim();
        if (otherUserId.Length == 0)
        {
            throw ApiException.ValidationFailed("memberIds 不能包含空用户 ID");
        }

        if (string.Equals(otherUserId, currentUserId, StringComparison.Ordinal))
        {
            throw ApiException.ValidationFailed("不能与自己创建单聊");
        }

        var otherUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == otherUserId, cancellationToken);
        if (otherUser is null || otherUser.Status != UserStatus.Active)
        {
            throw new ApiException(ErrorCodes.NotFound, "目标用户不存在或不可用", StatusCodes.Status404NotFound);
        }

        var normalizedTitle = NormalizeTitle(title);
        var directKey = BuildDirectKey(currentUserId, otherUserId);

        var existing = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(conversation => conversation.DirectKey == directKey, cancellationToken);
        if (existing is not null)
        {
            return (await BuildAsync(new[] { existing }, currentUserId, cancellationToken))[0];
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new ConversationEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = ConversationType.Direct,
            Title = normalizedTitle,
            DirectKey = directKey,
            CreatedBy = currentUserId,
            CreatedAt = now,
            LastSeq = 0,
            LastMessageId = null,
        };

        _db.Conversations.Add(entity);
        _db.ConversationMembers.AddRange(
            new ConversationMemberEntity
            {
                ConversationId = entity.Id,
                UserId = currentUserId,
                Role = MemberRole.Owner,
                JoinedAt = now,
                LastReadSeq = 0,
            },
            new ConversationMemberEntity
            {
                ConversationId = entity.Id,
                UserId = otherUserId,
                Role = MemberRole.Member,
                JoinedAt = now,
                LastReadSeq = 0,
            });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // 并发创建同一单聊时由 conversations.direct_key 唯一索引兜底：回读已存在会话。
            _db.ChangeTracker.Clear();
            var concurrent = await _db.Conversations.AsNoTracking()
                .FirstOrDefaultAsync(conversation => conversation.DirectKey == directKey, cancellationToken);
            if (concurrent is null)
            {
                throw;
            }

            return (await BuildAsync(new[] { concurrent }, currentUserId, cancellationToken))[0];
        }

        _logger.LogInformation(
            "创建单聊会话：{ConversationId}（{CurrentUserId} ↔ {OtherUserId}）",
            entity.Id,
            currentUserId,
            otherUserId);

        return (await BuildAsync(new[] { entity }, currentUserId, cancellationToken))[0];
    }

    /// <summary>读取单个会话：不存在 NOT_FOUND，非成员 NOT_MEMBER。</summary>
    public async Task<Conversation> GetAsync(string currentUserId, string conversationId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        var isMember = await _db.ConversationMembers.AsNoTracking()
            .AnyAsync(item => item.ConversationId == conversationId && item.UserId == currentUserId, cancellationToken);
        if (!isMember)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);
        }

        return (await BuildAsync(new[] { conversation }, currentUserId, cancellationToken))[0];
    }

    /// <summary>把实体会话组装成协议 DTO（成员、我的 lastReadSeq、最新消息快照）。</summary>
    private async Task<IReadOnlyList<Conversation>> BuildAsync(
        IReadOnlyList<ConversationEntity> conversations,
        string currentUserId,
        CancellationToken cancellationToken)
    {
        if (conversations.Count == 0)
        {
            return Array.Empty<Conversation>();
        }

        var conversationIds = conversations.Select(conversation => conversation.Id).ToList();

        var memberRows = await (
                from member in _db.ConversationMembers.AsNoTracking()
                join user in _db.Users.AsNoTracking() on member.UserId equals user.Id
                where conversationIds.Contains(member.ConversationId)
                orderby member.JoinedAt
                select new
                {
                    member.ConversationId,
                    member.UserId,
                    user.DisplayName,
                    user.Email,
                    member.Role,
                    member.JoinedAt,
                    user.AvatarFileId,
                    member.LastReadSeq,
                })
            .ToListAsync(cancellationToken);

        var lastMessageIds = conversations
            .Where(conversation => conversation.LastMessageId is not null)
            .Select(conversation => conversation.LastMessageId!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        var lastMessages = new Dictionary<string, Message>(StringComparer.Ordinal);
        if (lastMessageIds.Count > 0)
        {
            var messageRows = await _db.Messages.AsNoTracking()
                .Where(message => lastMessageIds.Contains(message.Id))
                .ToListAsync(cancellationToken);
            foreach (var row in messageRows)
            {
                lastMessages[row.Id] = ToProtocolMessage(row);
            }
        }

        var result = new List<Conversation>(conversations.Count);
        foreach (var conversation in conversations)
        {
            var members = memberRows
                .Where(row => string.Equals(row.ConversationId, conversation.Id, StringComparison.Ordinal))
                .Select(row => new ConversationMember(
                    row.UserId,
                    row.DisplayName,
                    row.Email,
                    row.Role,
                    row.JoinedAt,
                    row.AvatarFileId))
                .ToList();

            var myLastReadSeq = memberRows
                .FirstOrDefault(row => string.Equals(row.ConversationId, conversation.Id, StringComparison.Ordinal) &&
                                       string.Equals(row.UserId, currentUserId, StringComparison.Ordinal))
                ?.LastReadSeq ?? 0L;

            Message? lastMessage = null;
            if (conversation.LastMessageId is not null &&
                lastMessages.TryGetValue(conversation.LastMessageId, out var message))
            {
                lastMessage = message;
            }

            result.Add(new Conversation(
                conversation.Id,
                conversation.Type,
                conversation.Title ?? string.Empty,
                conversation.CreatedAt,
                conversation.LastSeq,
                myLastReadSeq,
                members,
                lastMessage));
        }

        return result
            .OrderByDescending(conversation => conversation.LastMessage?.CreatedAt ?? conversation.CreatedAt)
            .ToList();
    }

    /// <summary>messages 实体 → 协议 Message 对象（content_json 原样解析为两态 content）。</summary>
    public static Message ToProtocolMessage(MessageEntity message)
    {
        using var document = JsonDocument.Parse(message.ContentJson);
        return new Message(
            message.Id,
            message.ConversationId,
            message.SenderId,
            message.Seq,
            message.MsgType,
            MessageContent.FromElement(document.RootElement),
            message.CreatedAt,
            string.IsNullOrEmpty(message.ClientMsgId) ? null : message.ClientMsgId,
            message.ReplyTo,
            message.DeletedAt);
    }

    /// <summary>单聊唯一键：两个 UUID 升序（Ordinal）后以 ':' 拼接，与 schema.sql 约定一致。</summary>
    private static string BuildDirectKey(string firstUserId, string secondUserId) =>
        string.CompareOrdinal(firstUserId, secondUserId) <= 0
            ? $"{firstUserId}:{secondUserId}"
            : $"{secondUserId}:{firstUserId}";

    private static string? NormalizeTitle(string? title)
    {
        var normalized = title?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > TitleMaxLength)
        {
            throw ApiException.ValidationFailed($"会话标题最多 {TitleMaxLength} 个字符");
        }

        return normalized;
    }
}
