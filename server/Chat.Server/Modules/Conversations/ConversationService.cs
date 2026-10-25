using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using ConversationMemberEntity = Chat.Server.Data.Entities.ConversationMember;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Modules.Conversations;

/// <summary>群成员变更结果：最新会话 DTO、系统消息（无变化时为 null）、需推送的成员、被移除用户（退出/移除场景）。</summary>
public sealed record GroupMutationResult(
    Conversation Conversation,
    IReadOnlyList<string> MemberUserIds,
    Message? SystemMessage,
    string? RemovedUserId);

/// <summary>
/// 会话业务（M1-07 单聊最小集 + M3-04 群聊成员管理）：创建单聊（direct_key 去重）/群聊、
/// 当前用户会话列表、单会话读取（成员校验）、群成员邀请/退出/移除。
/// 输出 DTO 为 protocol/protocol.md §4.5 的 Conversation 对象，REST 与 WS conv.list 共用。
/// </summary>
public sealed class ConversationService
{
    private const int TitleMaxLength = 100;

    private readonly ChatDbContext _db;
    private readonly SystemMessageWriter _systemMessages;
    private readonly AppOptions _appOptions;
    private readonly ILogger<ConversationService> _logger;

    public ConversationService(
        ChatDbContext db,
        SystemMessageWriter systemMessages,
        IOptions<AppOptions> appOptions,
        ILogger<ConversationService> logger)
    {
        _db = db;
        _systemMessages = systemMessages;
        _appOptions = appOptions.Value;
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

    /// <summary>
    /// 创建群聊（M3-04）：校验群名/成员 → 事务内写会话、成员与 group.created 系统消息（seq 严格 +1）
    /// → 提交后返回最新 Conversation DTO 与系统消息（供 REST 向在线成员推送）。
    /// </summary>
    public async Task<GroupMutationResult> CreateGroupAsync(
        string currentUserId,
        string? title,
        IReadOnlyList<string>? memberIds,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeGroupTitle(title);
        var invitedIds = NormalizeMemberIds(memberIds, currentUserId);
        if (invitedIds.Count == 0)
        {
            throw ApiException.ValidationFailed("群聊至少需要邀请 1 位成员（不含群主）");
        }

        var maxMembers = Math.Clamp(_appOptions.GroupMaxMembers, 2, 500);
        if (invitedIds.Count + 1 > maxMembers)
        {
            throw ApiException.ValidationFailed($"群成员数量超过上限（最多 {maxMembers} 人，含群主）");
        }

        var creator = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == currentUserId, cancellationToken);
        if (creator is null || creator.Status != UserStatus.Active)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        var invitedUsers = await _db.Users.AsNoTracking()
            .Where(user => invitedIds.Contains(user.Id))
            .ToListAsync(cancellationToken);
        if (invitedUsers.Count != invitedIds.Count)
        {
            throw new ApiException(ErrorCodes.NotFound, "部分被邀请用户不存在", StatusCodes.Status404NotFound);
        }

        if (invitedUsers.Any(user => user.Status != UserStatus.Active))
        {
            throw new ApiException(ErrorCodes.NotFound, "部分被邀请用户不可用", StatusCodes.Status404NotFound);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new ConversationEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = ConversationType.Group,
            Title = normalizedTitle,
            DirectKey = null,
            CreatedBy = currentUserId,
            CreatedAt = now,
            LastSeq = 0,
            LastMessageId = null,
        };

        _db.Conversations.Add(entity);
        _db.ConversationMembers.Add(new ConversationMemberEntity
        {
            ConversationId = entity.Id,
            UserId = currentUserId,
            Role = MemberRole.Owner,
            JoinedAt = now,
            LastReadSeq = 0,
        });
        foreach (var invitedId in invitedIds)
        {
            _db.ConversationMembers.Add(new ConversationMemberEntity
            {
                ConversationId = entity.Id,
                UserId = invitedId,
                Role = MemberRole.Member,
                JoinedAt = now,
                LastReadSeq = 0,
            });
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var systemContent = new SystemContent(
            "group.created",
            currentUserId,
            null,
            $"{creator.DisplayName} 创建了群聊");
        var systemMessage = await _systemMessages.AppendAsync(entity, currentUserId, systemContent, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "创建群聊会话：{ConversationId}（成员 {MemberCount} 人，创建者 {UserId}）",
            entity.Id,
            invitedIds.Count + 1,
            currentUserId);

        var conversation = (await BuildAsync(new[] { entity }, currentUserId, cancellationToken))[0];
        var memberUserIds = invitedIds.Append(currentUserId).ToList();
        return new GroupMutationResult(conversation, memberUserIds, systemMessage, RemovedUserId: null);
    }

    /// <summary>
    /// 邀请成员（M3-04）：仅群聊；非成员 NOT_MEMBER；GroupOnlyOwnerCanInvite 时仅群主可邀请；
    /// 已在群内的用户幂等跳过；无新增时直接返回当前会话、不写系统消息、不推送。
    /// </summary>
    public async Task<GroupMutationResult> AddMembersAsync(
        string currentUserId,
        string conversationId,
        IReadOnlyList<string>? userIds,
        CancellationToken cancellationToken)
    {
        if (userIds is null || userIds.Count == 0)
        {
            throw ApiException.ValidationFailed("userIds 不能为空");
        }

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        if (conversation.Type != ConversationType.Group)
        {
            throw ApiException.ValidationFailed("仅群聊支持成员管理");
        }

        var members = await _db.ConversationMembers
            .Where(member => member.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        var currentMembership = members.FirstOrDefault(member => string.Equals(member.UserId, currentUserId, StringComparison.Ordinal));
        if (currentMembership is null)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);
        }

        if (_appOptions.GroupOnlyOwnerCanInvite && currentMembership.Role != MemberRole.Owner)
        {
            throw new ApiException(ErrorCodes.Forbidden, "仅群主可以邀请成员", StatusCodes.Status403Forbidden);
        }

        var existingIds = members.Select(member => member.UserId).ToHashSet(StringComparer.Ordinal);
        var candidateIds = NormalizeMemberIds(userIds, currentUserId)
            .Where(userId => !existingIds.Contains(userId))
            .ToList();

        if (candidateIds.Count == 0)
        {
            // 幂等：没有新成员时不写系统消息、不推送，直接返回当前会话。
            var current = (await BuildAsync(new[] { conversation }, currentUserId, cancellationToken))[0];
            return new GroupMutationResult(current, existingIds.ToList(), SystemMessage: null, RemovedUserId: null);
        }

        var maxMembers = Math.Clamp(_appOptions.GroupMaxMembers, 2, 500);
        if (members.Count + candidateIds.Count > maxMembers)
        {
            throw ApiException.ValidationFailed($"群成员数量超过上限（最多 {maxMembers} 人）");
        }

        var operatorUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == currentUserId, cancellationToken);
        if (operatorUser is null)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        var candidateUsers = await _db.Users.AsNoTracking()
            .Where(user => candidateIds.Contains(user.Id))
            .ToListAsync(cancellationToken);
        if (candidateUsers.Count != candidateIds.Count)
        {
            throw new ApiException(ErrorCodes.NotFound, "部分被邀请用户不存在", StatusCodes.Status404NotFound);
        }

        if (candidateUsers.Any(user => user.Status != UserStatus.Active))
        {
            throw new ApiException(ErrorCodes.NotFound, "部分被邀请用户不可用", StatusCodes.Status404NotFound);
        }

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var candidateId in candidateIds)
        {
            var newMember = new ConversationMemberEntity
            {
                ConversationId = conversationId,
                UserId = candidateId,
                Role = MemberRole.Member,
                JoinedAt = now,
                LastReadSeq = 0,
            };
            members.Add(newMember);
            _db.ConversationMembers.Add(newMember);
        }

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var nameById = candidateUsers.ToDictionary(user => user.Id, user => user.DisplayName, StringComparer.Ordinal);
        var invitedNames = candidateIds.Select(userId => nameById[userId]).ToList();
        var systemContent = new SystemContent(
            "member.joined",
            currentUserId,
            candidateIds,
            $"{operatorUser.DisplayName} 邀请 {string.Join("、", invitedNames)} 加入群聊");
        var systemMessage = await _systemMessages.AppendAsync(conversation, currentUserId, systemContent, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "群成员邀请：{ConversationId} 新增 {AddedCount} 人（操作者 {UserId}）",
            conversationId,
            candidateIds.Count,
            currentUserId);

        var updated = (await BuildAsync(new[] { conversation }, currentUserId, cancellationToken))[0];
        var memberUserIds = existingIds.Concat(candidateIds).ToList();
        return new GroupMutationResult(updated, memberUserIds, systemMessage, RemovedUserId: null);
    }

    /// <summary>
    /// 退出/移除群成员（M3-04）：目标=自己即退出（群主不可退出）；移除他人仅群主可操作。
    /// 成员变更与系统消息在同一事务内提交。
    /// </summary>
    public async Task<GroupMutationResult> RemoveMemberAsync(
        string currentUserId,
        string conversationId,
        string targetUserId,
        CancellationToken cancellationToken)
    {
        var normalizedTargetId = (targetUserId ?? string.Empty).Trim();
        if (normalizedTargetId.Length == 0)
        {
            throw ApiException.ValidationFailed("userId 不能为空");
        }

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken);
        if (conversation is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        }

        if (conversation.Type != ConversationType.Group)
        {
            throw ApiException.ValidationFailed("仅群聊支持成员管理");
        }

        var members = await _db.ConversationMembers
            .Where(member => member.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        var currentMembership = members.FirstOrDefault(member => string.Equals(member.UserId, currentUserId, StringComparison.Ordinal));
        if (currentMembership is null)
        {
            throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);
        }

        var targetMembership = members.FirstOrDefault(member => string.Equals(member.UserId, normalizedTargetId, StringComparison.Ordinal));
        if (targetMembership is null)
        {
            throw new ApiException(ErrorCodes.NotFound, "该用户不是群成员", StatusCodes.Status404NotFound);
        }

        var operatorUser = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == currentUserId, cancellationToken);
        if (operatorUser is null)
        {
            throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        }

        var isSelf = string.Equals(normalizedTargetId, currentUserId, StringComparison.Ordinal);
        SystemContent systemContent;
        if (isSelf)
        {
            if (currentMembership.Role == MemberRole.Owner)
            {
                throw ApiException.ValidationFailed("群主不能退出群聊（v1 不支持转让群主）");
            }

            systemContent = new SystemContent(
                "member.left",
                currentUserId,
                null,
                $"{operatorUser.DisplayName} 退出了群聊");
        }
        else
        {
            if (currentMembership.Role != MemberRole.Owner)
            {
                throw new ApiException(ErrorCodes.Forbidden, "只有群主可以移除成员", StatusCodes.Status403Forbidden);
            }

            var targetUser = await _db.Users.AsNoTracking()
                .FirstOrDefaultAsync(user => user.Id == normalizedTargetId, cancellationToken);
            var targetName = targetUser?.DisplayName ?? "该成员";
            systemContent = new SystemContent(
                "member.removed",
                currentUserId,
                new[] { normalizedTargetId },
                $"{operatorUser.DisplayName} 将 {targetName} 移出群聊");
        }

        _db.ConversationMembers.Remove(targetMembership);
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);

        var systemMessage = await _systemMessages.AppendAsync(conversation, currentUserId, systemContent, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation(
            "群成员变更：{ConversationId} 移除 {TargetUserId}（{Event}，操作者 {UserId}）",
            conversationId,
            normalizedTargetId,
            systemContent.Event,
            currentUserId);

        var updated = (await BuildAsync(new[] { conversation }, currentUserId, cancellationToken))[0];
        var remainingMemberIds = members
            .Where(member => !string.Equals(member.UserId, normalizedTargetId, StringComparison.Ordinal))
            .Select(member => member.UserId)
            .ToList();
        return new GroupMutationResult(updated, remainingMemberIds, systemMessage, RemovedUserId: normalizedTargetId);
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

    /// <summary>群名：必填、去首尾空格、1~100 字符（计划 §4.6，v1 建群后不改名）。</summary>
    private static string NormalizeGroupTitle(string? title)
    {
        var normalized = title?.Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw ApiException.ValidationFailed("群聊名称不能为空");
        }

        if (normalized.Length > TitleMaxLength)
        {
            throw ApiException.ValidationFailed($"群聊名称最多 {TitleMaxLength} 个字符");
        }

        return normalized;
    }

    /// <summary>成员 ID 去重、去空、去自己，保持传入顺序（用于建群与邀请）。</summary>
    private static List<string> NormalizeMemberIds(IReadOnlyList<string>? memberIds, string currentUserId)
    {
        var result = new List<string>();
        if (memberIds is null)
        {
            return result;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in memberIds)
        {
            var userId = (raw ?? string.Empty).Trim();
            if (userId.Length == 0)
            {
                throw ApiException.ValidationFailed("memberIds 不能包含空用户 ID");
            }

            if (string.Equals(userId, currentUserId, StringComparison.Ordinal))
            {
                continue;
            }

            if (seen.Add(userId))
            {
                result.Add(userId);
            }
        }

        return result;
    }
}
