using System.Security.Cryptography;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Blocks;
using Chat.Server.Modules.Messages;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using ConversationMemberEntity = Chat.Server.Data.Entities.ConversationMember;
using ConversationDto = Chat.Protocol.Frames.Conversation;
using GroupInviteLinkEntity = Chat.Server.Data.Entities.GroupInviteLink;
using GroupInviteLinkDto = Chat.Protocol.Frames.GroupInviteLink;
using MessageDto = Chat.Protocol.Frames.Message;

namespace Chat.Server.Modules.Conversations;

/// <summary>兑换群邀请链接的结果：最新会话 DTO、系统消息（幂等命中时为 null）、需推送的全体成员。</summary>
public sealed record GroupInviteJoinResult(
    ConversationDto Conversation,
    MessageDto? SystemMessage,
    IReadOnlyList<string> MemberUserIds);

/// <summary>
/// 群邀请链接（C1c）：创建/列出/撤销/预览/兑换。
/// 只处理「加入群聊」；「加入服务」与「添加好友」的邀请在各自规则确定后单独实现。
/// 令牌为不含邮箱的随机串，日志不记录令牌。
/// </summary>
public sealed class GroupInviteService
{
    /// <summary>链接有效期：7 天。</summary>
    public const long InviteTtlMilliseconds = 7L * 24 * 60 * 60 * 1000;

    private const int TokenByteLength = 32;

    private readonly ChatDbContext _db;
    private readonly ConversationService _conversations;
    private readonly SystemMessageWriter _systemMessages;
    private readonly BlockService _blocks;
    private readonly IOptions<AppOptions> _appOptions;

    public GroupInviteService(
        ChatDbContext db,
        ConversationService conversations,
        SystemMessageWriter systemMessages,
        BlockService blocks,
        IOptions<AppOptions> appOptions)
    {
        _db = db;
        _conversations = conversations;
        _systemMessages = systemMessages;
        _blocks = blocks;
        _appOptions = appOptions;
    }

    /// <summary>创建群邀请链接：须为群成员；GroupOnlyOwnerCanInvite 时仅群主可创建。</summary>
    public async Task<GroupInviteLinkDto> CreateAsync(
        string currentUserId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await EnsureInvitePermissionAsync(currentUserId, conversationId, cancellationToken);

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var entity = new GroupInviteLinkEntity
        {
            Token = GenerateToken(),
            ConversationId = conversationId,
            CreatedBy = currentUserId,
            ExpiresAt = now + InviteTtlMilliseconds,
            RevokedAt = null,
            CreatedAt = now,
        };
        _db.GroupInviteLinks.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);
        return ToDto(entity);
    }

    /// <summary>列出该群未撤销的邀请链接：须为群成员；GroupOnlyOwnerCanInvite 时仅群主可查看。</summary>
    public async Task<IReadOnlyList<GroupInviteLinkDto>> ListAsync(
        string currentUserId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        await EnsureInvitePermissionAsync(currentUserId, conversationId, cancellationToken);

        var links = await _db.GroupInviteLinks.AsNoTracking()
            .Where(link => link.ConversationId == conversationId && link.RevokedAt == null)
            .OrderByDescending(link => link.CreatedAt)
            .ToListAsync(cancellationToken);
        return links.Select(ToDto).ToList();
    }

    /// <summary>撤销邀请链接：群主可撤销任意链接，创建者可撤销自己的链接；重复撤销幂等。</summary>
    public async Task RevokeAsync(
        string currentUserId,
        string conversationId,
        string token,
        CancellationToken cancellationToken)
    {
        var (_, membership) = await EnsureInvitePermissionAsync(currentUserId, conversationId, cancellationToken);

        var link = await _db.GroupInviteLinks
            .FirstOrDefaultAsync(
                item => item.ConversationId == conversationId && item.Token == token,
                cancellationToken);
        if (link is null)
        {
            throw new ApiException(ErrorCodes.InviteInvalid, "邀请链接不存在", StatusCodes.Status404NotFound);
        }

        if (membership.Role != MemberRole.Owner && !string.Equals(link.CreatedBy, currentUserId, StringComparison.Ordinal))
        {
            throw new ApiException(ErrorCodes.Forbidden, "只有群主或创建者可以撤销该邀请链接", StatusCodes.Status403Forbidden);
        }

        if (link.RevokedAt is null)
        {
            link.RevokedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>预览：打开链接先确认目标群；过期/撤销/已在群内都在预览里返回，由用户决定是否加入。</summary>
    public async Task<GroupInvitePreview> PreviewAsync(
        string currentUserId,
        string token,
        CancellationToken cancellationToken)
    {
        var link = await FindLinkAsync(token, cancellationToken);
        var conversation = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == link.ConversationId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.InviteInvalid, "邀请链接对应的群不存在", StatusCodes.Status404NotFound);
        var creator = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == link.CreatedBy, cancellationToken);
        var members = await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == link.ConversationId)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        return new GroupInvitePreview(
            conversation.Id,
            conversation.Title ?? string.Empty,
            members.Count,
            Math.Clamp(_appOptions.Value.GroupMaxMembers, 2, 500),
            creator?.DisplayName ?? string.Empty,
            link.ExpiresAt,
            link.ExpiresAt <= now,
            link.RevokedAt is not null,
            members.Contains(currentUserId, StringComparer.Ordinal));
    }

    /// <summary>
    /// 兑换邀请链接：校验有效期/撤销状态/发起人当前权限/拉黑关系/群容量，成员变更与兑换在同一事务内完成；
    /// 重复或并发兑换幂等返回当前会话，不产生重复成员记录。
    /// </summary>
    public async Task<GroupInviteJoinResult> JoinAsync(
        string currentUserId,
        string token,
        CancellationToken cancellationToken)
    {
        var link = await FindLinkAsync(token, cancellationToken);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (link.RevokedAt is not null)
        {
            throw new ApiException(ErrorCodes.InviteRevoked, "邀请链接已撤销", StatusCodes.Status410Gone);
        }

        if (link.ExpiresAt <= now)
        {
            throw new ApiException(ErrorCodes.InviteExpired, "邀请链接已过期", StatusCodes.Status410Gone);
        }

        var conversation = await _db.Conversations
            .FirstOrDefaultAsync(item => item.Id == link.ConversationId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.InviteInvalid, "邀请链接对应的群不存在", StatusCodes.Status404NotFound);

        var memberIds = await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversation.Id)
            .Select(member => member.UserId)
            .ToListAsync(cancellationToken);

        // 已在群内：幂等返回，不重复写成员行与系统消息。
        if (memberIds.Contains(currentUserId, StringComparer.Ordinal))
        {
            return new GroupInviteJoinResult(
                await _conversations.GetAsync(currentUserId, conversation.Id, cancellationToken),
                SystemMessage: null,
                memberIds);
        }

        // 发起人当前权限：仍在群内；GroupOnlyOwnerCanInvite 时仍须是群主。
        var creatorMembership = await _db.ConversationMembers.AsNoTracking()
            .FirstOrDefaultAsync(
                member => member.ConversationId == conversation.Id && member.UserId == link.CreatedBy,
                cancellationToken);
        if (creatorMembership is null)
        {
            throw new ApiException(ErrorCodes.InviteInvalid, "邀请人已不在群内，链接已失效", StatusCodes.Status403Forbidden);
        }

        if (_appOptions.Value.GroupOnlyOwnerCanInvite && creatorMembership.Role != MemberRole.Owner)
        {
            throw new ApiException(ErrorCodes.InviteInvalid, "邀请人已无邀请权限，链接已失效", StatusCodes.Status403Forbidden);
        }

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == currentUserId, cancellationToken)
            ?? throw ApiException.Unauthorized("登录状态已失效，请重新登录");
        if (user.Status != UserStatus.Active)
        {
            throw new ApiException(ErrorCodes.AccountDisabled, "账号当前不可用", StatusCodes.Status403Forbidden);
        }

        await _blocks.EnsureInteractionAllowedAsync(currentUserId, link.CreatedBy, cancellationToken);

        var maxMembers = Math.Clamp(_appOptions.Value.GroupMaxMembers, 2, 500);
        MessageDto? systemMessage = null;
        var joined = false;

        await using (var transaction = await _db.Database.BeginTransactionAsync(cancellationToken))
        {
            // 先执行一次空更新取得 SQLite 写锁：与「直接加人」共用同一把锁，避免两条入口
            // 并发时都按旧人数通过容量检查、最终把群撑过上限。
            await _db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE conversations SET last_seq = last_seq WHERE id = {conversation.Id}",
                cancellationToken);
            // 写锁内重新加载会话，保证新成员 LastReadSeq 取到最新 last_seq。
            await _db.Entry(conversation).ReloadAsync(cancellationToken);

            // 事务内重新读取成员数：SQLite 写事务串行，避免并发兑换把群撑过上限。
            var currentMemberIds = await _db.ConversationMembers
                .Where(member => member.ConversationId == conversation.Id)
                .Select(member => member.UserId)
                .ToListAsync(cancellationToken);

            if (!currentMemberIds.Contains(currentUserId, StringComparer.Ordinal))
            {
                if (currentMemberIds.Count >= maxMembers)
                {
                    throw ApiException.ValidationFailed($"群成员数量已达上限（最多 {maxMembers} 人）");
                }

                _db.ConversationMembers.Add(new ConversationMemberEntity
                {
                    ConversationId = conversation.Id,
                    UserId = currentUserId,
                    Role = MemberRole.Member,
                    JoinedAt = now,
                    // 新成员加入前历史消息不计未读；加入系统消息计 1 条未读（protocol §4.6）。
                    LastReadSeq = conversation.LastSeq,
                });
                await _db.SaveChangesAsync(cancellationToken);

                systemMessage = await _systemMessages.AppendAsync(
                    conversation,
                    currentUserId,
                    new SystemContent(
                        "member.joined",
                        currentUserId,
                        new[] { currentUserId },
                        $"{user.DisplayName} 通过邀请链接加入群聊"),
                    cancellationToken);
                await _db.SaveChangesAsync(cancellationToken);
                joined = true;
            }

            await transaction.CommitAsync(cancellationToken);
        }

        var result = await _conversations.GetAsync(currentUserId, conversation.Id, cancellationToken);
        var memberUserIds = await _conversations.GetMemberUserIdsAsync(currentUserId, conversation.Id, cancellationToken);
        return new GroupInviteJoinResult(result, joined ? systemMessage : null, memberUserIds);
    }

    /// <summary>加载链接；不存在统一返回 INVITE_INVALID，避免枚举令牌。</summary>
    private async Task<GroupInviteLinkEntity> FindLinkAsync(string token, CancellationToken cancellationToken)
    {
        var normalized = (token ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new ApiException(ErrorCodes.InviteInvalid, "邀请链接无效", StatusCodes.Status404NotFound);
        }

        var link = await _db.GroupInviteLinks.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Token == normalized, cancellationToken)
            ?? throw new ApiException(ErrorCodes.InviteInvalid, "邀请链接无效", StatusCodes.Status404NotFound);
        return link;
    }

    /// <summary>校验群邀请资格：群聊、成员身份、GroupOnlyOwnerCanInvite 配置。</summary>
    private async Task<(ConversationEntity Conversation, ConversationMemberEntity Membership)> EnsureInvitePermissionAsync(
        string currentUserId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);
        if (conversation.Type != ConversationType.Group)
        {
            throw ApiException.ValidationFailed("仅群聊支持邀请链接");
        }

        var membership = await _db.ConversationMembers.AsNoTracking()
            .FirstOrDefaultAsync(
                member => member.ConversationId == conversationId && member.UserId == currentUserId,
                cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotMember, "你不是该会话成员", StatusCodes.Status403Forbidden);

        if (_appOptions.Value.GroupOnlyOwnerCanInvite && membership.Role != MemberRole.Owner)
        {
            throw new ApiException(ErrorCodes.Forbidden, "仅群主可以管理邀请链接", StatusCodes.Status403Forbidden);
        }

        return (conversation, membership);
    }

    /// <summary>生成不含邮箱的随机令牌（base64url，无填充）。</summary>
    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenByteLength);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static GroupInviteLinkDto ToDto(GroupInviteLinkEntity entity) => new(
        entity.Token,
        entity.ConversationId,
        entity.ExpiresAt,
        entity.RevokedAt,
        entity.CreatedAt);
}
