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
}
