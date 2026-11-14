using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Conversations;
using Chat.Server.Modules.Files;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 会话 REST 边界（M1-07 单聊 + M3-04 群聊，计划 §7）：创建单聊/群聊、当前用户会话列表、
/// 单会话读取；群成员邀请/退出/移除（M3-04 第二提交）。
/// 响应为 protocol/protocol.md §4.5 的 Conversation 对象（与 apps/web/src/types/frames.d.ts 一致）。
/// </summary>
public static class ConversationsEndpoints
{
    public static IEndpointRouteBuilder MapConversationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations", ListAsync)
            .WithName("ListConversations")
            .RequireAuthorization()
            .Produces<IReadOnlyList<Conversation>>();

        app.MapPost("/api/conversations", CreateAsync)
            .WithName("CreateConversation")
            .RequireAuthorization()
            .Produces<Conversation>();

        app.MapGet("/api/conversations/{conversationId}", GetAsync)
            .WithName("GetConversation")
            .RequireAuthorization()
            .Produces<Conversation>();

        app.MapPost("/api/conversations/{conversationId}/members", AddMembersAsync)
            .WithName("AddConversationMembers")
            .RequireAuthorization()
            .Produces<Conversation>();

        app.MapDelete("/api/conversations/{conversationId}/members/{userId}", RemoveMemberAsync)
            .WithName("RemoveConversationMember")
            .RequireAuthorization();

        // F-10/F-34：当前用户自己的置顶/免打扰/标为未读（置顶/免打扰不广播；标为未读只推本人多端）。
        app.MapPatch("/api/conversations/{conversationId}/member-settings", UpdateMemberSettingsAsync)
            .WithName("UpdateConversationMemberSettings")
            .RequireAuthorization()
            .Produces<Conversation>();

        // F-14：群主修改群名/公告；按收件人视角逐连接推送 conv.updated。
        app.MapPatch("/api/conversations/{conversationId}", UpdateGroupProfileAsync)
            .WithName("UpdateGroupProfile")
            .RequireAuthorization()
            .Produces<Conversation>();

        // F-24：群主转让；系统消息先于 conv.updated，conv.updated 按成员各自视角逐连接推送。
        app.MapPost("/api/conversations/{conversationId}/owner", TransferOwnerAsync)
            .WithName("TransferConversationOwner")
            .RequireAuthorization()
            .Produces<Conversation>();

        // F-36：群置顶消息（仅群主；每群最多一条；单聊不支持）。
        app.MapPut("/api/conversations/{conversationId}/pinned-message", SetPinnedMessageAsync)
            .WithName("SetConversationPinnedMessage")
            .RequireAuthorization()
            .Produces<Conversation>();

        app.MapDelete("/api/conversations/{conversationId}/pinned-message", ClearPinnedMessageAsync)
            .WithName("ClearConversationPinnedMessage")
            .RequireAuthorization()
            .Produces<Conversation>();

        // F-15：会话共享图片/文件汇总（只读分页；type=image|file、beforeSeq、limit）。
        app.MapGet("/api/conversations/{conversationId}/files", GetSharedFilesAsync)
            .WithName("ListConversationFiles")
            .RequireAuthorization()
            .Produces<ConversationFilesPage>();

        // C2c：会话共享链接汇总（只读分页；beforeSeq、limit）。
        app.MapGet("/api/conversations/{conversationId}/links", GetSharedLinksAsync)
            .WithName("ListConversationLinks")
            .RequireAuthorization()
            .Produces<ConversationLinksPage>();

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var list = await conversations.ListAsync(userId, cancellationToken);
        return Results.Json(list);
    }

    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        CreateConversationRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var type = request.Type?.Trim().ToLowerInvariant();

        if (type == "direct")
        {
            var conversation = await conversations.CreateDirectAsync(
                userId,
                request.Title,
                request.MemberIds,
                cancellationToken);

            await PushConvUpdatedAsync(
                conversations,
                notifier,
                conversation.ConversationId,
                conversation.Members.Select(member => member.UserId).ToArray(),
                cancellationToken);
            return Results.Json(conversation);
        }

        if (type == "group")
        {
            var result = await conversations.CreateGroupAsync(
                userId,
                request.Title,
                request.MemberIds,
                cancellationToken);

            // 先推系统消息（message.new），再按收件人视角推完整会话（conv.updated）；两者都只发给在线成员。
            if (result.SystemMessage is not null)
            {
                notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            }

            await PushConvUpdatedAsync(
                conversations,
                notifier,
                result.Conversation.ConversationId,
                result.MemberUserIds,
                cancellationToken);
            return Results.Json(result.Conversation);
        }

        throw ApiException.ValidationFailed("type 必须是 direct 或 group");
    }

    /// <summary>邀请成员（M3-04）：批量邀请，返回更新后的 Conversation；无新增时不写系统消息、不推送。</summary>
    private static async Task<IResult> AddMembersAsync(
        ClaimsPrincipal principal,
        string conversationId,
        MemberChangeRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var result = await conversations.AddMembersAsync(userId, conversationId, request.UserIds, cancellationToken);

        // 有变更才推送：message.new（系统消息）先于 conv.updated（按收件人视角，见 §4.5）。
        if (result.SystemMessage is not null)
        {
            notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            await PushConvUpdatedAsync(
                conversations,
                notifier,
                result.Conversation.ConversationId,
                result.MemberUserIds,
                cancellationToken);
        }

        return Results.Json(result.Conversation);
    }

    /// <summary>
    /// 退出/移除群成员（M3-04）：目标=自己即退出（群主不可退出）；成功返回 204。
    /// 剩余成员收 message.new + conv.updated，被移除/退出的用户额外收 removedConversationIds 帧。
    /// </summary>
    private static async Task<IResult> RemoveMemberAsync(
        ClaimsPrincipal principal,
        string conversationId,
        string userId,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await conversations.RemoveMemberAsync(currentUserId, conversationId, userId, cancellationToken);

        if (result.SystemMessage is not null)
        {
            notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            await PushConvUpdatedAsync(
                conversations,
                notifier,
                result.Conversation.ConversationId,
                result.MemberUserIds,
                cancellationToken);
        }

        if (result.RemovedUserId is not null)
        {
            notifier.PushRemoved(result.RemovedUserId, conversationId);
        }

        return Results.NoContent();
    }

    /// <summary>
    /// 按收件人视角推送 conv.updated（协议 §4.5）：每个成员只看到自己的 pinned / muted /
    /// lastReadSeq / unread，不能把操作者视角的会话广播给其他成员。
    /// </summary>
    private static async Task PushConvUpdatedAsync(
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        string conversationId,
        IReadOnlyList<string> memberUserIds,
        CancellationToken cancellationToken)
    {
        var perRecipient = await conversations.BuildPerRecipientAsync(
            conversationId,
            memberUserIds,
            cancellationToken);
        notifier.PushUpdatedPerRecipient(perRecipient);
    }

    /// <summary>
    /// 更新当前用户的会话成员设置（F-10/F-34）：body 至少包含 pinned/muted/markedUnread 之一；只改本人成员行并返回最新 Conversation。
    /// 私密性要求：pinned/muted 不广播 conv.updated；markedUnread 仅向本人全部在线连接推送按本人视角的 conv.updated
    /// （同账号多端同步），其他成员不可见。
    /// </summary>
    private static async Task<IResult> UpdateMemberSettingsAsync(
        ClaimsPrincipal principal,
        string conversationId,
        MemberSettingsRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var conversation = await conversations.UpdateMemberSettingsAsync(
            currentUserId,
            conversationId,
            request.Pinned,
            request.Muted,
            cancellationToken,
            request.MarkedUnread);

        // 标为未读需要同账号多端同步（F-34）：只推给本人的在线连接，其他成员不可见。
        if (request.MarkedUnread is not null)
        {
            notifier.PushUpdatedPerRecipient(
                new Dictionary<string, Conversation>(StringComparer.Ordinal)
                {
                    [currentUserId] = conversation,
                });
        }

        return Results.Json(conversation);
    }

    /// <summary>
    /// 修改群资料（F-14 / F-23）：仅群聊群主可改；body 至少包含 title/announcement/avatarFileId 之一，
    /// announcement/avatarFileId 传空串表示清空；返回当前用户视角的完整 Conversation。
    /// conv.updated 按每个成员的视角分别构建后逐连接推送（D5：不得全体推同一份）。
    /// </summary>
    private static async Task<IResult> UpdateGroupProfileAsync(
        ClaimsPrincipal principal,
        string conversationId,
        GroupProfileRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await conversations.UpdateGroupProfileAsync(
            currentUserId,
            conversationId,
            request.Title,
            request.Announcement,
            request.AvatarFileId,
            cancellationToken);

        var perRecipient = await conversations.BuildPerRecipientAsync(
            result.Conversation,
            result.MemberUserIds,
            cancellationToken);
        notifier.PushUpdatedPerRecipient(perRecipient);

        return Results.Json(perRecipient[currentUserId]);
    }

    /// <summary>
    /// 转让群主（F-24）：仅群主可操作，body 为目标成员 userId；成功后角色互换并写 owner.transferred 系统消息。
    /// conv.updated 按每个成员的视角分别构建后逐连接推送（保留各自 pinned/muted/lastReadSeq）。
    /// </summary>
    private static async Task<IResult> TransferOwnerAsync(
        ClaimsPrincipal principal,
        string conversationId,
        TransferOwnerRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await conversations.TransferOwnerAsync(
            currentUserId,
            conversationId,
            request.UserId,
            cancellationToken);

        // 有变更才推送：message.new（系统消息）先于 conv.updated。
        notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
        var perRecipient = await conversations.BuildPerRecipientAsync(
            result.Conversation,
            result.MemberUserIds,
            cancellationToken);
        notifier.PushUpdatedPerRecipient(perRecipient);

        return Results.Json(perRecipient[currentUserId]);
    }


    /// <summary>群置顶消息设置/替换（F-36）：仅群主，body { messageId }；成功后按收件人视角推 conv.updated。</summary>
    private static async Task<IResult> SetPinnedMessageAsync(
        ClaimsPrincipal principal,
        string conversationId,
        PinnedMessageRequest request,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await conversations.SetPinnedMessageAsync(
            currentUserId,
            conversationId,
            request.MessageId,
            cancellationToken);
        var perRecipient = await conversations.BuildPerRecipientAsync(
            result.Conversation,
            result.MemberUserIds,
            cancellationToken);
        notifier.PushUpdatedPerRecipient(perRecipient);
        return Results.Json(perRecipient[currentUserId]);
    }

    /// <summary>群置顶消息取消（F-36）：仅群主，无置顶时幂等；成功后按收件人视角推 conv.updated。</summary>
    private static async Task<IResult> ClearPinnedMessageAsync(
        ClaimsPrincipal principal,
        string conversationId,
        ConversationService conversations,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await conversations.ClearPinnedMessageAsync(
            currentUserId,
            conversationId,
            cancellationToken);
        var perRecipient = await conversations.BuildPerRecipientAsync(
            result.Conversation,
            result.MemberUserIds,
            cancellationToken);
        notifier.PushUpdatedPerRecipient(perRecipient);
        return Results.Json(perRecipient[currentUserId]);
    }

    /// <summary>
    /// 会话共享图片/文件汇总（F-15）：type=image|file 必填、beforeSeq 排他游标、limit 1~100 默认 50；
    /// 只统计 messages.file_id 非空的 image/file 消息，按文件去重取最新引用 seq 倒序分页。
    /// </summary>
    private static async Task<IResult> GetSharedFilesAsync(
        ClaimsPrincipal principal,
        string conversationId,
        string? type,
        long? beforeSeq,
        int? limit,
        ConversationFilesService files,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var page = await files.GetSharedFilesAsync(
            userId,
            conversationId,
            type,
            beforeSeq,
            limit,
            cancellationToken);
        return Results.Json(page);
    }

    private static async Task<IResult> GetSharedLinksAsync(
        ClaimsPrincipal principal,
        string conversationId,
        long? beforeSeq,
        int? beforeOffset,
        int? limit,
        ConversationFilesService files,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var page = await files.GetSharedLinksAsync(
            userId,
            conversationId,
            beforeSeq,
            beforeOffset,
            limit,
            cancellationToken);
        return Results.Json(page);
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        string conversationId,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var conversation = await conversations.GetAsync(userId, conversationId, cancellationToken);
        return Results.Json(conversation);
    }

    /// <summary>
    /// 创建会话请求（与 apps/web/src/api/conversations.ts 的 CreateConversationRequest 对齐）：
    /// type=direct 时 memberIds 恰好一个对方用户；type=group 时 title 为群名（1~100 字符）、memberIds 为邀请成员。
    /// </summary>
    public sealed record CreateConversationRequest(string? Type, string? Title, IReadOnlyList<string>? MemberIds);

    /// <summary>群成员变更请求（M3-04）：批量邀请的成员用户 ID 列表。</summary>
    public sealed record MemberChangeRequest(IReadOnlyList<string>? UserIds);

    /// <summary>成员设置请求（F-10 / 标为未读）：pinned/muted/markedUnread 至少提供一个，由 Service 校验。</summary>
    public sealed record MemberSettingsRequest(bool? Pinned = null, bool? Muted = null, bool? MarkedUnread = null);

    /// <summary>群资料更新请求（F-14 / F-23）：title/announcement/avatarFileId 至少一个非 null；announcement/avatarFileId 传空串表示清空。</summary>
    public sealed record GroupProfileRequest(string? Title = null, string? Announcement = null, string? AvatarFileId = null);

    /// <summary>F-36：群置顶消息请求（PUT body）。</summary>
    public sealed record PinnedMessageRequest(string? MessageId = null);

    /// <summary>群主转让请求（F-24）：目标成员用户 ID。</summary>
    public sealed record TransferOwnerRequest(string? UserId);
}
