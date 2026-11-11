using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Conversations;
using Microsoft.EntityFrameworkCore;
using ProtocolMessage = Chat.Protocol.Frames.Message;

namespace Chat.Server.Modules.Favorites;

/// <summary>
/// 个人收藏条目（F-38）：原始引用 + 当前可访问状态。
/// <see cref="Message"/> 仅在 <see cref="Available"/> 为 true 时非空；不可用条目客户端不得展示正文/附件。
/// </summary>
public sealed record FavoriteItemDto(
    string MessageId,
    string ConversationId,
    string ConversationTitle,
    string SenderName,
    bool Available,
    long FavoritedAt,
    ProtocolMessage? Message,
    /// <summary>发送者头像 files.id（2026-11-11 追加，可空；旧客户端忽略该字段）。</summary>
    string? SenderAvatarFileId = null);

/// <summary>
/// 个人收藏（F-38，protocol/protocol.md §7 第 13 条）：SQLite 为事实源，只保存账号 + 原消息引用 + 收藏时间。
/// 列表读取时按原消息最新状态计算可访问性（已撤回/已离开会话 → 不可用）；新增/取消幂等，可安全重试。
/// </summary>
public sealed class FavoritesService
{
    /// <summary>单账号收藏上限（与旧本地版一致）。</summary>
    public const int MaxFavorites = 500;

    private const int MessageIdMaxLength = 64;

    private static readonly MessageType[] FavoritableTypes =
    [
        MessageType.Text,
        MessageType.Image,
        MessageType.File,
        MessageType.Voice,
    ];

    private readonly ChatDbContext _db;
    private readonly ILogger<FavoritesService> _logger;

    public FavoritesService(ChatDbContext db, ILogger<FavoritesService> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>当前账号的收藏列表（按收藏时间倒序）；原消息不可访问时 Available=false 且 Message=null。</summary>
    public async Task<IReadOnlyList<FavoriteItemDto>> ListAsync(string userId, CancellationToken cancellationToken)
    {
        var favorites = await _db.Favorites.AsNoTracking()
            .Where(favorite => favorite.UserId == userId)
            .OrderByDescending(favorite => favorite.CreatedAt)
            .ThenByDescending(favorite => favorite.MessageId)
            .ToListAsync(cancellationToken);

        if (favorites.Count == 0)
        {
            return [];
        }

        var messageIds = favorites.Select(favorite => favorite.MessageId).Distinct(StringComparer.Ordinal).ToList();
        var messages = await _db.Messages.AsNoTracking()
            .Where(message => messageIds.Contains(message.Id))
            .ToDictionaryAsync(message => message.Id, cancellationToken);

        var conversationIds = messages.Values.Select(message => message.ConversationId).Distinct(StringComparer.Ordinal).ToList();
        var memberships = await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.UserId == userId && conversationIds.Contains(member.ConversationId))
            .Select(member => member.ConversationId)
            .ToListAsync(cancellationToken);
        var memberSet = memberships.ToHashSet(StringComparer.Ordinal);

        var conversations = await _db.Conversations.AsNoTracking()
            .Where(conversation => conversationIds.Contains(conversation.Id))
            .ToDictionaryAsync(conversation => conversation.Id, cancellationToken);

        var senderIds = messages.Values.Select(message => message.SenderId).Distinct(StringComparer.Ordinal).ToList();
        var senderProfiles = await _db.Users.AsNoTracking()
            .Where(user => senderIds.Contains(user.Id))
            .Select(user => new { user.Id, user.DisplayName, user.AvatarFileId })
            .ToDictionaryAsync(row => row.Id, row => row, cancellationToken);

        var directTitles = await LoadDirectConversationTitlesAsync(userId, conversations.Values, cancellationToken);

        var items = new List<FavoriteItemDto>(favorites.Count);
        foreach (var favorite in favorites)
        {
            if (!messages.TryGetValue(favorite.MessageId, out var message))
            {
                items.Add(new FavoriteItemDto(favorite.MessageId, string.Empty, string.Empty, string.Empty, false, favorite.CreatedAt, null));
                continue;
            }

            var available = message.DeletedAt is null && memberSet.Contains(message.ConversationId);
            conversations.TryGetValue(message.ConversationId, out var conversation);
            senderProfiles.TryGetValue(message.SenderId, out var sender);
            items.Add(new FavoriteItemDto(
                favorite.MessageId,
                message.ConversationId,
                ConversationTitleOf(conversation, directTitles),
                sender?.DisplayName ?? "未知用户",
                available,
                favorite.CreatedAt,
                available ? ConversationService.ToProtocolMessage(message) : null,
                sender?.AvatarFileId));
        }

        return items;
    }

    /// <summary>新增收藏；已收藏时幂等返回现有条目，达到 500 条上限时返回 VALIDATION_FAILED。</summary>
    public async Task<FavoriteItemDto> AddAsync(string userId, string? messageId, CancellationToken cancellationToken)
    {
        var id = NormalizeMessageId(messageId);

        var existing = await _db.Favorites.AsNoTracking()
            .FirstOrDefaultAsync(favorite => favorite.UserId == userId && favorite.MessageId == id, cancellationToken);
        if (existing is not null)
        {
            return await BuildItemAsync(existing, cancellationToken);
        }

        var message = await _db.Messages.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == id, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "消息不存在", 404);

        if (message.DeletedAt is not null)
        {
            throw ApiException.ValidationFailed("已撤回的消息不能收藏");
        }

        if (!FavoritableTypes.Contains(message.MsgType))
        {
            throw ApiException.ValidationFailed("该消息类型不支持收藏");
        }

        var isMember = await _db.ConversationMembers.AsNoTracking()
            .AnyAsync(member => member.UserId == userId && member.ConversationId == message.ConversationId, cancellationToken);
        if (!isMember)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", 403);
        }

        // F-38 收尾：500 上限判断与插入在同一条 SQL 内原子执行，避免并发下超过上限；
        // ON CONFLICT DO NOTHING 让重复收藏幂等，不产生第二条记录。
        var createdAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var inserted = await _db.Database.ExecuteSqlInterpolatedAsync(
            $"""
            INSERT INTO favorites (user_id, message_id, created_at)
            SELECT {userId}, {id}, {createdAt}
            WHERE (SELECT COUNT(*) FROM favorites WHERE user_id = {userId}) < {MaxFavorites}
            ON CONFLICT(user_id, message_id) DO NOTHING
            """,
            cancellationToken);
        if (inserted == 0)
        {
            var conflicted = await _db.Favorites.AsNoTracking()
                .FirstOrDefaultAsync(row => row.UserId == userId && row.MessageId == id, cancellationToken);
            if (conflicted is not null)
            {
                // 并发重复收藏：另一请求已写入，按幂等返回现有条目。
                return await BuildItemAsync(conflicted, cancellationToken);
            }

            throw ApiException.ValidationFailed($"收藏已达上限（最多 {MaxFavorites} 条）");
        }

        var favorite = new Favorite { UserId = userId, MessageId = id, CreatedAt = createdAt };
        _logger.LogInformation("收藏新增：用户 {UserId} 消息 {MessageId}", userId, id);
        return await BuildItemAsync(favorite, cancellationToken);
    }

    /// <summary>取消收藏；不存在时同样视为成功（幂等），返回是否实际删除。</summary>
    public async Task<bool> RemoveAsync(string userId, string? messageId, CancellationToken cancellationToken)
    {
        var id = NormalizeMessageId(messageId);
        var removed = await _db.Favorites
            .Where(favorite => favorite.UserId == userId && favorite.MessageId == id)
            .ExecuteDeleteAsync(cancellationToken);
        if (removed > 0)
        {
            _logger.LogInformation("收藏取消：用户 {UserId} 消息 {MessageId}", userId, id);
        }

        return removed > 0;
    }

    private async Task<FavoriteItemDto> BuildItemAsync(Favorite favorite, CancellationToken cancellationToken)
    {
        var message = await _db.Messages.AsNoTracking().FirstOrDefaultAsync(row => row.Id == favorite.MessageId, cancellationToken);
        if (message is null)
        {
            return new FavoriteItemDto(favorite.MessageId, string.Empty, string.Empty, string.Empty, false, favorite.CreatedAt, null);
        }

        var isMember = await _db.ConversationMembers.AsNoTracking()
            .AnyAsync(member => member.UserId == favorite.UserId && member.ConversationId == message.ConversationId, cancellationToken);
        var conversation = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(row => row.Id == message.ConversationId, cancellationToken);
        var sender = await _db.Users.AsNoTracking()
            .Where(user => user.Id == message.SenderId)
            .Select(user => new { user.DisplayName, user.AvatarFileId })
            .FirstOrDefaultAsync(cancellationToken);

        var directTitles = conversation is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : await LoadDirectConversationTitlesAsync(favorite.UserId, [conversation], cancellationToken);
        var available = message.DeletedAt is null && isMember;

        return new FavoriteItemDto(
            favorite.MessageId,
            message.ConversationId,
            ConversationTitleOf(conversation, directTitles),
            sender?.DisplayName ?? "未知用户",
            available,
            favorite.CreatedAt,
            available ? ConversationService.ToProtocolMessage(message) : null,
            sender?.AvatarFileId);
    }

    private async Task<IReadOnlyDictionary<string, string>> LoadDirectConversationTitlesAsync(
        string userId,
        IEnumerable<Conversation> conversations,
        CancellationToken cancellationToken)
    {
        var directIds = conversations
            .Where(conversation => conversation.Type == ConversationType.Direct)
            .Select(conversation => conversation.Id)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (directIds.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var peers = await (
            from member in _db.ConversationMembers.AsNoTracking()
            join user in _db.Users.AsNoTracking() on member.UserId equals user.Id
            where directIds.Contains(member.ConversationId) && member.UserId != userId
            select new { member.ConversationId, user.DisplayName }).ToListAsync(cancellationToken);

        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var peer in peers)
        {
            if (!string.IsNullOrWhiteSpace(peer.DisplayName))
            {
                titles.TryAdd(peer.ConversationId, peer.DisplayName);
            }
        }

        return titles;
    }

    private static string ConversationTitleOf(Conversation? conversation, IReadOnlyDictionary<string, string> directTitles)
    {
        if (conversation is null)
        {
            return string.Empty;
        }

        if (conversation.Type == ConversationType.Group)
        {
            return string.IsNullOrWhiteSpace(conversation.Title) ? "群聊" : conversation.Title!.Trim();
        }

        return directTitles.GetValueOrDefault(conversation.Id) ?? "单聊";
    }

    private static string NormalizeMessageId(string? messageId)
    {
        var id = messageId?.Trim();
        if (string.IsNullOrEmpty(id) || id.Length > MessageIdMaxLength)
        {
            throw ApiException.ValidationFailed($"messageId 不能为空且长度不超过 {MessageIdMaxLength}");
        }

        return id;
    }
}
