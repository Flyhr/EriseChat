using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Modules.Blocks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using FriendEntity = Chat.Server.Data.Entities.Friend;
using FriendRequestEntity = Chat.Server.Data.Entities.FriendRequest;
using UserEntity = Chat.Server.Data.Entities.User;

namespace Chat.Server.Modules.Friends;

/// <summary>好友申请列表（仅 pending）：发给我的（incoming）与我发出的（outgoing）。</summary>
public sealed record FriendRequestsResult(FriendRequest[] Incoming, FriendRequest[] Outgoing);

/// <summary>发起好友申请的结果：申请快照；反向 pending 自动接受时 AutoAccepted=true 并附带好友快照。</summary>
public sealed record SendFriendRequestResult(FriendRequest Request, bool AutoAccepted, Friend? Friend);

/// <summary>接受申请的结果：更新后的申请快照与「接受方视角」的好友对象。</summary>
public sealed record AcceptFriendResult(FriendRequest Request, Friend Friend);

/// <summary>删除好友的结果：双方向的好友快照（各自视角，用于推送 friend.removed）。</summary>
public sealed record RemoveFriendResult(Friend Own, Friend Other);

/// <summary>
/// 好友关系业务（F-17）：好友列表、申请（发送/接受/拒绝/撤销）、备注与分组、删除好友。
/// 输出 DTO 为 protocol/protocol.md §4.9 的 Friend / FriendRequest 对象；WS 推送由
/// <see cref="FriendRealtimeNotifier"/> 在端点层触发（本服务不依赖实时层，便于单测）。
/// </summary>
public sealed class FriendService
{
    /// <summary>申请附言上限（protocol/protocol.md §4.9）。</summary>
    public const int MessageMaxLength = 100;

    /// <summary>备注上限（字符）。</summary>
    public const int RemarkMaxLength = 64;

    /// <summary>分组名上限（字符）。</summary>
    public const int GroupNameMaxLength = 32;

    /// <summary>每人同时处于 pending 的发出申请上限（防滥用）。</summary>
    public const int PendingOutgoingLimit = 50;

    private readonly ChatDbContext _db;
    private readonly BlockService _blocks;
    private readonly ILogger<FriendService> _logger;

    public FriendService(ChatDbContext db, BlockService blocks, ILogger<FriendService> logger)
    {
        _db = db;
        _blocks = blocks;
        _logger = logger;
    }

    private static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>当前用户的好友列表：按分组名（未分组排最后）、备注/昵称、用户 ID 排序。</summary>
    public async Task<IReadOnlyList<Friend>> ListAsync(string currentUserId, CancellationToken cancellationToken)
    {
        var rows = await _db.Friends.AsNoTracking()
            .Where(friend => friend.OwnerUserId == currentUserId)
            .Join(_db.Users.AsNoTracking(), friend => friend.FriendUserId, user => user.Id,
                (friend, user) => new { friend, user })
            .ToListAsync(cancellationToken);

        return rows
            .OrderBy(row => string.IsNullOrEmpty(row.friend.GroupName) ? 1 : 0)
            .ThenBy(row => row.friend.GroupName, StringComparer.Ordinal)
            .ThenBy(row => string.IsNullOrEmpty(row.friend.Remark) ? 1 : 0)
            .ThenBy(row => ResolveDisplayName(row.friend.Remark, row.user.DisplayName), StringComparer.Ordinal)
            .ThenBy(row => row.user.Id, StringComparer.Ordinal)
            .Select(row => ToFriend(row.friend, row.user))
            .ToList();
    }

    /// <summary>申请列表：仅 pending，按创建时间倒序；incoming 为别人发给我的，outgoing 为我发出的。</summary>
    public async Task<FriendRequestsResult> ListRequestsAsync(string currentUserId, CancellationToken cancellationToken)
    {
        var rows = await _db.FriendRequests.AsNoTracking()
            .Where(request => request.Status == FriendRequestStatus.Pending &&
                             (request.ToUserId == currentUserId || request.FromUserId == currentUserId))
            .OrderByDescending(request => request.CreatedAt)
            .ToListAsync(cancellationToken);

        var userIds = rows
            .SelectMany(request => new[] { request.FromUserId, request.ToUserId })
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var users = await _db.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        var incoming = rows
            .Where(request => request.ToUserId == currentUserId)
            .Select(request => ToRequest(request, users))
            .ToArray();
        var outgoing = rows
            .Where(request => request.FromUserId == currentUserId)
            .Select(request => ToRequest(request, users))
            .ToArray();

        return new FriendRequestsResult(incoming, outgoing);
    }

    /// <summary>
    /// 发起好友申请（F-17）：自己/目标不存在或不可用、已是好友均拒绝；同向 pending 幂等返回同一条；
    /// 反向 pending 时自动接受（接受原申请并建立双向好友关系）。
    /// </summary>
    public async Task<SendFriendRequestResult> SendRequestAsync(
        string currentUserId,
        string? toUserId,
        string? message,
        CancellationToken cancellationToken)
    {
        var targetUserId = (toUserId ?? string.Empty).Trim();
        if (targetUserId.Length == 0)
        {
            throw ApiException.ValidationFailed("toUserId 不能为空");
        }

        if (string.Equals(targetUserId, currentUserId, StringComparison.Ordinal))
        {
            throw ApiException.ValidationFailed("不能添加自己为好友");
        }

        var note = NormalizeOptional(message);
        if (note is not null && note.Length > MessageMaxLength)
        {
            throw ApiException.ValidationFailed($"附言不能超过 {MessageMaxLength} 个字符");
        }

        var target = await _db.Users.AsNoTracking()
            .FirstOrDefaultAsync(user => user.Id == targetUserId, cancellationToken)
            ?? throw ApiException.NotFound("目标用户不存在");
        if (target.Status != UserStatus.Active)
        {
            throw ApiException.ValidationFailed("该账号当前不可用");
        }

        // C1b：任一方拉黑时不允许再次申请好友；放在幂等分支之前，重试也返回 BLOCKED。
        await _blocks.EnsureInteractionAllowedAsync(currentUserId, targetUserId, cancellationToken);

        var alreadyFriend = await _db.Friends.AsNoTracking()
            .AnyAsync(friend => friend.OwnerUserId == currentUserId && friend.FriendUserId == targetUserId,
                cancellationToken);
        if (alreadyFriend)
        {
            throw ApiException.ValidationFailed("对方已经是你的好友");
        }

        var now = Now();

        // 同向 pending：幂等返回已有申请（不重复落库、不重复推送）。
        var sameDirection = await _db.FriendRequests.AsNoTracking()
            .FirstOrDefaultAsync(request => request.FromUserId == currentUserId &&
                                            request.ToUserId == targetUserId &&
                                            request.Status == FriendRequestStatus.Pending,
                cancellationToken);
        if (sameDirection is not null)
        {
            return new SendFriendRequestResult(
                await BuildRequestAsync(sameDirection, cancellationToken),
                AutoAccepted: false,
                Friend: null);
        }

        // 反向 pending：自动接受，等价于对方申请 + 我同意（事务内完成并返回双向好友）。
        var reverse = await _db.FriendRequests
            .FirstOrDefaultAsync(request => request.FromUserId == targetUserId &&
                                            request.ToUserId == currentUserId &&
                                            request.Status == FriendRequestStatus.Pending,
                cancellationToken);
        if (reverse is not null)
        {
            await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
            reverse.Status = FriendRequestStatus.Accepted;
            reverse.HandledAt = now;

            var friend = await AddFriendshipRowsAsync(currentUserId, targetUserId, now, cancellationToken);
            await _db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            _logger.LogInformation("好友申请自动接受：{FromUserId} ↔ {ToUserId}", targetUserId, currentUserId);

            return new SendFriendRequestResult(
                await BuildRequestAsync(reverse, cancellationToken),
                AutoAccepted: true,
                Friend: friend);
        }

        var pendingOutgoing = await _db.FriendRequests.AsNoTracking()
            .CountAsync(request => request.FromUserId == currentUserId &&
                                   request.Status == FriendRequestStatus.Pending,
                cancellationToken);
        if (pendingOutgoing >= PendingOutgoingLimit)
        {
            throw ApiException.RateLimited("你发出的好友申请过多，请等待对方处理后再试");
        }

        var entity = new FriendRequestEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            FromUserId = currentUserId,
            ToUserId = targetUserId,
            Message = note,
            Status = FriendRequestStatus.Pending,
            CreatedAt = now,
        };
        _db.FriendRequests.Add(entity);
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("好友申请：{FromUserId} → {ToUserId}", currentUserId, targetUserId);
        return new SendFriendRequestResult(
            await BuildRequestAsync(entity, cancellationToken),
            AutoAccepted: false,
            Friend: null);
    }

    /// <summary>接受我收到的 pending 申请：申请人 + 我建立双向好友关系（已存在的行保留原备注/分组）。</summary>
    public async Task<AcceptFriendResult> AcceptAsync(
        string currentUserId,
        string requestId,
        CancellationToken cancellationToken)
    {
        var request = await _db.FriendRequests
            .FirstOrDefaultAsync(item => item.Id == requestId &&
                                         item.ToUserId == currentUserId &&
                                         item.Status == FriendRequestStatus.Pending,
                cancellationToken)
            ?? throw ApiException.NotFound("好友申请不存在或已处理");

        // C1b：拉黑之后不再接受此前落库的申请（例如申请在拉黑前发出、拉黑后才被点接受）。
        await _blocks.EnsureInteractionAllowedAsync(currentUserId, request.FromUserId, cancellationToken);

        var now = Now();
        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        request.Status = FriendRequestStatus.Accepted;
        request.HandledAt = now;

        var friend = await AddFriendshipRowsAsync(currentUserId, request.FromUserId, now, cancellationToken);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("接受好友申请：{FromUserId} ↔ {ToUserId}", request.FromUserId, currentUserId);
        return new AcceptFriendResult(await BuildRequestAsync(request, cancellationToken), friend);
    }

    /// <summary>拒绝我收到的 pending 申请。</summary>
    public async Task<FriendRequest> RejectAsync(string currentUserId, string requestId, CancellationToken cancellationToken)
    {
        var request = await _db.FriendRequests
            .FirstOrDefaultAsync(item => item.Id == requestId &&
                                         item.ToUserId == currentUserId &&
                                         item.Status == FriendRequestStatus.Pending,
                cancellationToken)
            ?? throw ApiException.NotFound("好友申请不存在或已处理");

        request.Status = FriendRequestStatus.Rejected;
        request.HandledAt = Now();
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("拒绝好友申请：{FromUserId} → {ToUserId}", request.FromUserId, currentUserId);
        return await BuildRequestAsync(request, cancellationToken);
    }

    /// <summary>撤销我发出的 pending 申请。</summary>
    public async Task<FriendRequest> CancelAsync(string currentUserId, string requestId, CancellationToken cancellationToken)
    {
        var request = await _db.FriendRequests
            .FirstOrDefaultAsync(item => item.Id == requestId &&
                                         item.FromUserId == currentUserId &&
                                         item.Status == FriendRequestStatus.Pending,
                cancellationToken)
            ?? throw ApiException.NotFound("好友申请不存在或已处理");

        request.Status = FriendRequestStatus.Cancelled;
        request.HandledAt = Now();
        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("撤销好友申请：{FromUserId} → {ToUserId}", currentUserId, request.ToUserId);
        return await BuildRequestAsync(request, cancellationToken);
    }

    /// <summary>修改我对好友的备注/分组（null 表示不修改，空串表示清空）。</summary>
    public async Task<Friend> UpdateFriendAsync(
        string currentUserId,
        string friendUserId,
        string? remark,
        string? groupName,
        CancellationToken cancellationToken)
    {
        var row = await _db.Friends
            .FirstOrDefaultAsync(item => item.OwnerUserId == currentUserId && item.FriendUserId == friendUserId,
                cancellationToken)
            ?? throw ApiException.NotFound("对方不是你的好友");

        if (remark is not null)
        {
            var value = NormalizeOptional(remark);
            if (value is not null && value.Length > RemarkMaxLength)
            {
                throw ApiException.ValidationFailed($"备注不能超过 {RemarkMaxLength} 个字符");
            }

            row.Remark = value;
        }

        if (groupName is not null)
        {
            var value = NormalizeOptional(groupName);
            if (value is not null && value.Length > GroupNameMaxLength)
            {
                throw ApiException.ValidationFailed($"分组名不能超过 {GroupNameMaxLength} 个字符");
            }

            row.GroupName = value;
        }

        await _db.SaveChangesAsync(cancellationToken);

        var user = await _db.Users.AsNoTracking()
            .FirstAsync(item => item.Id == friendUserId, cancellationToken);
        return ToFriend(row, user);
    }

    /// <summary>删除好友：事务内删除双向两行（保留会话与历史消息），返回双方视角快照。</summary>
    public async Task<RemoveFriendResult> RemoveAsync(
        string currentUserId,
        string friendUserId,
        CancellationToken cancellationToken)
    {
        var rows = await _db.Friends
            .Where(item =>
                (item.OwnerUserId == currentUserId && item.FriendUserId == friendUserId) ||
                (item.OwnerUserId == friendUserId && item.FriendUserId == currentUserId))
            .ToListAsync(cancellationToken);

        var mine = rows.FirstOrDefault(item => item.OwnerUserId == currentUserId && item.FriendUserId == friendUserId)
                   ?? throw ApiException.NotFound("对方不是你的好友");
        var reverse = rows.FirstOrDefault(item => item.OwnerUserId == friendUserId && item.FriendUserId == currentUserId);

        var users = await _db.Users.AsNoTracking()
            .Where(user => user.Id == currentUserId || user.Id == friendUserId)
            .ToDictionaryAsync(user => user.Id, cancellationToken);

        var currentUser = users[currentUserId];
        var otherUser = users[friendUserId];

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);
        _db.Friends.RemoveRange(rows);
        await _db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        _logger.LogInformation("删除好友：{CurrentUserId} ✕ {FriendUserId}", currentUserId, friendUserId);

        return new RemoveFriendResult(
            ToFriend(mine, otherUser),
            reverse is not null
                ? ToFriend(reverse, currentUser)
                : new Friend(
                    currentUserId,
                    currentUser.DisplayName,
                    currentUser.Email,
                    mine.CreatedAt,
                    Remark: null,
                    GroupName: null,
                    AvatarFileId: currentUser.AvatarFileId));
    }

    /// <summary>返回「owner 视角」的好友对象（用于推送另一方向的好友快照）；不是好友时抛 NotFound。</summary>
    public async Task<Friend> GetFriendViewAsync(
        string ownerUserId,
        string otherUserId,
        CancellationToken cancellationToken)
    {
        var row = await _db.Friends.AsNoTracking()
            .FirstOrDefaultAsync(item => item.OwnerUserId == ownerUserId && item.FriendUserId == otherUserId,
                cancellationToken)
            ?? throw ApiException.NotFound("对方不是你的好友");
        var user = await _db.Users.AsNoTracking()
            .FirstAsync(item => item.Id == otherUserId, cancellationToken);
        return ToFriend(row, user);
    }

    /// <summary>建立双向好友行（已存在的行保留原有备注/分组），返回「当前用户视角」的好友对象。</summary>
    private async Task<Friend> AddFriendshipRowsAsync(
        string currentUserId,
        string otherUserId,
        long createdAt,
        CancellationToken cancellationToken)
    {
        var existing = await _db.Friends
            .Where(item =>
                (item.OwnerUserId == currentUserId && item.FriendUserId == otherUserId) ||
                (item.OwnerUserId == otherUserId && item.FriendUserId == currentUserId))
            .ToListAsync(cancellationToken);

        if (!existing.Any(item => item.OwnerUserId == currentUserId && item.FriendUserId == otherUserId))
        {
            _db.Friends.Add(new FriendEntity
            {
                OwnerUserId = currentUserId,
                FriendUserId = otherUserId,
                CreatedAt = createdAt,
            });
        }

        if (!existing.Any(item => item.OwnerUserId == otherUserId && item.FriendUserId == currentUserId))
        {
            _db.Friends.Add(new FriendEntity
            {
                OwnerUserId = otherUserId,
                FriendUserId = currentUserId,
                CreatedAt = createdAt,
            });
        }

        var other = await _db.Users.AsNoTracking()
            .FirstAsync(user => user.Id == otherUserId, cancellationToken);
        return new Friend(
            other.Id,
            other.DisplayName,
            other.Email,
            createdAt,
            Remark: null,
            GroupName: null,
            AvatarFileId: other.AvatarFileId);
    }

    private async Task<FriendRequest> BuildRequestAsync(
        FriendRequestEntity request,
        CancellationToken cancellationToken)
    {
        var userIds = new[] { request.FromUserId, request.ToUserId };
        var users = await _db.Users.AsNoTracking()
            .Where(user => userIds.Contains(user.Id))
            .ToDictionaryAsync(user => user.Id, cancellationToken);
        return ToRequest(request, users);
    }

    private static FriendRequest ToRequest(FriendRequestEntity request, IReadOnlyDictionary<string, UserEntity> users) =>
        new(
            request.Id,
            request.FromUserId,
            users.TryGetValue(request.FromUserId, out var from) ? from.DisplayName : string.Empty,
            request.ToUserId,
            users.TryGetValue(request.ToUserId, out var to) ? to.DisplayName : string.Empty,
            request.Status,
            request.CreatedAt,
            request.Message,
            request.HandledAt);

    private static Friend ToFriend(FriendEntity entity, UserEntity user) =>
        new(
            user.Id,
            user.DisplayName,
            user.Email,
            entity.CreatedAt,
            entity.Remark,
            entity.GroupName,
            user.AvatarFileId);

    private static string ResolveDisplayName(string? remark, string displayName) =>
        string.IsNullOrWhiteSpace(remark) ? displayName : remark;

    private static string? NormalizeOptional(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }
}
