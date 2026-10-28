using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Messages;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 消息 REST 边界（计划 §7、project-overview §7.5）：M1-08 提供历史分页，
/// 正常收发走 WebSocket；F-11 提供全局/会话内消息搜索（REST，不属于协议帧）。
/// 响应形状与 apps/web/src/api/messages.ts 的 MessagePage / MessageSearchPage 对齐。
/// </summary>
public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations/{conversationId}/messages", GetHistoryAsync)
            .WithName("GetMessageHistory")
            .RequireAuthorization()
            .Produces<MessagePageResponse>();

        // F-11：GET /api/messages/search?q=&conversationId?=&beforeSeq?=&limit=
        // 仅搜索本人会话内 text 消息；q 1~50、limit 1~50（默认 20）；按 seq 降序返回。
        app.MapGet("/api/messages/search", SearchAsync)
            .WithName("SearchMessages")
            .RequireAuthorization()
            .Produces<MessageSearchPageResponse>();

        return app;
    }

    private static async Task<IResult> GetHistoryAsync(
        string conversationId,
        long? beforeSeq,
        int? limit,
        ClaimsPrincipal principal,
        MessageService messages,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var page = await messages.GetHistoryAsync(userId, conversationId, beforeSeq, limit, cancellationToken);
        return Results.Json(new MessagePageResponse(page.Messages, page.HasMore));
    }

    /// <summary>历史分页响应：messages 按 seq 升序；hasMore 表示还有更早消息（对齐 MessagePage）。</summary>
    public sealed record MessagePageResponse(IReadOnlyList<Message> Messages, bool HasMore);

    private static async Task<IResult> SearchAsync(
        string? q,
        string? conversationId,
        long? beforeSeq,
        int? limit,
        ClaimsPrincipal principal,
        MessageService messages,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var page = await messages.SearchAsync(userId, q, conversationId, beforeSeq, limit, cancellationToken);
        return Results.Json(new MessageSearchPageResponse(page.Messages, page.HasMore));
    }

    /// <summary>搜索响应（F-11，非协议帧）：messages 按 seq 降序；hasMore 表示可继续翻候选。</summary>
    public sealed record MessageSearchPageResponse(IReadOnlyList<Message> Messages, bool HasMore);
}
