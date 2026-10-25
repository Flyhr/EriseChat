using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Conversations;
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

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
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
        var userId = GetUserId(principal);
        var type = request.Type?.Trim().ToLowerInvariant();

        if (type == "direct")
        {
            var conversation = await conversations.CreateDirectAsync(
                userId,
                request.Title,
                request.MemberIds,
                cancellationToken);

            notifier.PushUpdated(new[] { conversation });
            return Results.Json(conversation);
        }

        if (type == "group")
        {
            var result = await conversations.CreateGroupAsync(
                userId,
                request.Title,
                request.MemberIds,
                cancellationToken);

            // 先推系统消息（message.new），再推完整会话（conv.updated）；两者都只发给在线成员。
            if (result.SystemMessage is not null)
            {
                notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            }

            notifier.PushUpdated(new[] { result.Conversation });
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
        var userId = GetUserId(principal);
        var result = await conversations.AddMembersAsync(userId, conversationId, request.UserIds, cancellationToken);

        // 有变更才推送：message.new（系统消息）先于 conv.updated（含全体成员的最新会话）。
        if (result.SystemMessage is not null)
        {
            notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            notifier.PushUpdated(new[] { result.Conversation });
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
        var currentUserId = GetUserId(principal);
        var result = await conversations.RemoveMemberAsync(currentUserId, conversationId, userId, cancellationToken);

        if (result.SystemMessage is not null)
        {
            notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            notifier.PushUpdated(new[] { result.Conversation });
        }

        if (result.RemovedUserId is not null)
        {
            notifier.PushRemoved(result.RemovedUserId, conversationId);
        }

        return Results.NoContent();
    }

    private static async Task<IResult> GetAsync(
        ClaimsPrincipal principal,
        string conversationId,
        ConversationService conversations,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var conversation = await conversations.GetAsync(userId, conversationId, cancellationToken);
        return Results.Json(conversation);
    }

    private static string GetUserId(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");

    /// <summary>
    /// 创建会话请求（与 apps/web/src/api/conversations.ts 的 CreateConversationRequest 对齐）：
    /// type=direct 时 memberIds 恰好一个对方用户；type=group 时 title 为群名（1~100 字符）、memberIds 为邀请成员。
    /// </summary>
    public sealed record CreateConversationRequest(string? Type, string? Title, IReadOnlyList<string>? MemberIds);

    /// <summary>群成员变更请求（M3-04）：批量邀请的成员用户 ID 列表。</summary>
    public sealed record MemberChangeRequest(IReadOnlyList<string>? UserIds);
}
