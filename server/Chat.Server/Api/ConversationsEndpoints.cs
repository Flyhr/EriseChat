using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Conversations;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 会话 REST 边界（M1-07 最小集，计划 §7）：创建单聊、当前用户会话列表、单会话读取。
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

        // M1 只实现单聊；群聊在 M3 批次接入（避免现在写入半成品语义）。
        if (!string.Equals(request.Type?.Trim(), "direct", StringComparison.OrdinalIgnoreCase))
        {
            throw ApiException.ValidationFailed("当前版本仅支持单聊（type=direct）");
        }

        var conversation = await conversations.CreateDirectAsync(
            userId,
            request.Title,
            request.MemberIds,
            cancellationToken);

        notifier.PushUpdated(new[] { conversation });
        return Results.Json(conversation);
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
    /// 创建会话请求（与 apps/web/src/api/conversations.ts 的 CreateConversationRequest 对齐）。
    /// M1 仅支持 type=direct + memberIds 恰好一个对方用户。
    /// </summary>
    public sealed record CreateConversationRequest(string? Type, string? Title, IReadOnlyList<string>? MemberIds);
}
