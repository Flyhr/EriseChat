using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using BlockEntity = Chat.Server.Data.Entities.Block;

namespace Chat.Server.Modules.Blocks;

/// <summary>
/// 拉黑关系业务（C1b）：有方向的单向行，任一方拉黑期间双方不能私聊、转发到对方私聊、
/// 再次申请好友或直接邀请对方入群；共同群消息与历史消息读取不受影响。
/// 拉黑不改变好友关系，解除拉黑只删除对应行，权限按当时的会话/好友状态恢复。
/// 规则校验由 FriendService、ConversationService、MessageService 在各业务入口调用
/// （服务端为唯一事实源）。
/// </summary>
public sealed class BlockService
{
    /// <summary>好友申请、群邀请等交互的统一提示：不指明是哪一方拉黑。</summary>
    public const string InteractionBlockedMessage = "你与对方存在拉黑关系，无法完成该操作";

    /// <summary>单聊发送/转发的提示。</summary>
    public const string DirectMessageBlockedMessage = "你与对方存在拉黑关系，无法发送消息";

    private readonly ChatDbContext _db;

    public BlockService(ChatDbContext db) => _db = db;

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>双方任意方向是否存在拉黑（同一用户恒为 false）。</summary>
    public async Task<bool> IsBlockedAsync(string firstUserId, string secondUserId, CancellationToken cancellationToken)
    {
        if (string.Equals(firstUserId, secondUserId, StringComparison.Ordinal))
        {
            return false;
        }

        return await _db.Blocks.AsNoTracking().AnyAsync(
            block => (block.BlockerUserId == firstUserId && block.BlockedUserId == secondUserId) ||
                     (block.BlockerUserId == secondUserId && block.BlockedUserId == firstUserId),
            cancellationToken);
    }

    /// <summary>好友申请、群邀请、新建单聊等交互入口：任一方拉黑时抛出 BLOCKED。</summary>
    public async Task EnsureInteractionAllowedAsync(
        string currentUserId,
        string otherUserId,
        CancellationToken cancellationToken)
    {
        if (await IsBlockedAsync(currentUserId, otherUserId, cancellationToken))
        {
            throw new ApiException(
                ErrorCodes.Blocked,
                InteractionBlockedMessage,
                StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>单聊发送/转发入口：任一方拉黑时抛出 BLOCKED（调用方需先确认会话是单聊）。</summary>
    public async Task EnsureDirectMessageAllowedAsync(
        string senderId,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var otherUserId = await _db.ConversationMembers.AsNoTracking()
            .Where(member => member.ConversationId == conversationId && member.UserId != senderId)
            .Select(member => member.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (otherUserId is null)
        {
            return;
        }

        if (await IsBlockedAsync(senderId, otherUserId, cancellationToken))
        {
            throw new ApiException(
                ErrorCodes.Blocked,
                DirectMessageBlockedMessage,
                StatusCodes.Status403Forbidden);
        }
    }

    /// <summary>拉黑（幂等）：已存在时返回原条目，不重复写入、不重复计时。</summary>
    public async Task<BlockedUser> BlockAsync(
        string currentUserId,
        string? targetUserId,
        CancellationToken cancellationToken)
    {
        var target = (targetUserId ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            throw ApiException.ValidationFailed("userId 不能为空");
        }

        if (string.Equals(target, currentUserId, StringComparison.Ordinal))
        {
            throw ApiException.ValidationFailed("不能拉黑自己");
        }

        var user = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == target, cancellationToken)
            ?? throw ApiException.NotFound("目标用户不存在");

        var existing = await _db.Blocks
            .FirstOrDefaultAsync(
                block => block.BlockerUserId == currentUserId && block.BlockedUserId == target,
                cancellationToken);
        if (existing is null)
        {
            existing = new BlockEntity
            {
                BlockerUserId = currentUserId,
                BlockedUserId = target,
                CreatedAt = Now(),
            };
            _db.Blocks.Add(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return new BlockedUser(user.Id, user.DisplayName, user.AvatarFileId, existing.CreatedAt);
    }

    /// <summary>解除拉黑（幂等）：不存在时同样成功，便于重试。</summary>
    public async Task UnblockAsync(
        string currentUserId,
        string? targetUserId,
        CancellationToken cancellationToken)
    {
        var target = (targetUserId ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            throw ApiException.ValidationFailed("userId 不能为空");
        }

        await _db.Blocks
            .Where(block => block.BlockerUserId == currentUserId && block.BlockedUserId == target)
            .ExecuteDeleteAsync(cancellationToken);
    }

    /// <summary>我拉黑的用户列表（按拉黑时间倒序）。</summary>
    public async Task<IReadOnlyList<BlockedUser>> ListAsync(
        string currentUserId,
        CancellationToken cancellationToken)
    {
        return await (from block in _db.Blocks.AsNoTracking()
                      join user in _db.Users.AsNoTracking() on block.BlockedUserId equals user.Id
                      where block.BlockerUserId == currentUserId
                      orderby block.CreatedAt descending, user.DisplayName
                      select new BlockedUser(user.Id, user.DisplayName, user.AvatarFileId, block.CreatedAt))
            .ToListAsync(cancellationToken);
    }
}
