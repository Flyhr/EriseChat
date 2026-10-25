using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Chat.Server.Modules.Messages;

/// <summary>已读上报结果：更新后的已读游标 + 单聊对方用户 ID（群聊为 null，不广播）+ 游标是否实际推进。</summary>
public sealed record ReadUpdateResult(long LastReadSeq, string? OtherUserId, bool Changed);

/// <summary>
/// 已读业务（M3-05，protocol/protocol.md §4.6）：校验成员与游标范围，按
/// max(现有 last_read_seq, 上报值) 只增不减更新 conversation_members.last_read_seq；
/// 单聊返回对方 userId 供 WS 层推送 read.updated，群聊 v1 只更新不广播。
/// </summary>
public sealed class ReadService
{
    private readonly ChatDbContext _db;
    private readonly ILogger<ReadService> _logger;

    public ReadService(ChatDbContext db, ILogger<ReadService> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<ReadUpdateResult> UpdateReadAsync(
        string userId,
        string conversationId,
        long lastReadSeq,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(conversationId))
        {
            throw ApiException.ValidationFailed("conversationId 不能为空");
        }

        var conversation = await _db.Conversations.AsNoTracking()
            .FirstOrDefaultAsync(item => item.Id == conversationId, cancellationToken)
            ?? throw new ApiException(ErrorCodes.NotFound, "会话不存在", StatusCodes.Status404NotFound);

        var members = await _db.ConversationMembers
            .Where(item => item.ConversationId == conversationId)
            .ToListAsync(cancellationToken);

        var mine = members.FirstOrDefault(item => string.Equals(item.UserId, userId, StringComparison.Ordinal))
            ?? throw new ApiException(ErrorCodes.NotMember, "不是该会话成员", StatusCodes.Status403Forbidden);

        if (lastReadSeq < 0 || lastReadSeq > conversation.LastSeq)
        {
            throw ApiException.ValidationFailed($"lastReadSeq 必须在 0~{conversation.LastSeq} 之间");
        }

        var previousSeq = mine.LastReadSeq;
        var newValue = Math.Max(previousSeq, lastReadSeq);
        if (newValue > previousSeq)
        {
            mine.LastReadSeq = newValue;
            await _db.SaveChangesAsync(cancellationToken);
        }

        var otherUserId = conversation.Type == ConversationType.Direct
            ? members.FirstOrDefault(item => !string.Equals(item.UserId, userId, StringComparison.Ordinal))?.UserId
            : null;

        var changed = newValue > previousSeq;
        _logger.LogInformation(
            "已读上报：会话 {ConversationId}，用户 {UserId}，原 {PreviousSeq}，新 {LastReadSeq}，推进 {Changed}",
            conversationId,
            userId,
            previousSeq,
            newValue,
            changed);

        return new ReadUpdateResult(newValue, otherUserId, changed);
    }
}
