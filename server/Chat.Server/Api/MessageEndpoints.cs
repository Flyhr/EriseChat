using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Messages;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 消息 REST 边界（计划 §7、project-overview §7.5）：M1-08 提供历史分页，
/// 正常收发走 WebSocket；响应形状与 apps/web/src/api/messages.ts 的 MessagePage 对齐。
/// </summary>
public static class MessageEndpoints
{
    public static IEndpointRouteBuilder MapMessageEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/conversations/{conversationId}/messages", GetHistoryAsync)
            .WithName("GetMessageHistory")
            .RequireAuthorization()
            .Produces<MessagePageResponse>();

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
        var userId = principal.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");
        var page = await messages.GetHistoryAsync(userId, conversationId, beforeSeq, limit, cancellationToken);
        return Results.Json(new MessagePageResponse(page.Messages, page.HasMore));
    }

    /// <summary>历史分页响应：messages 按 seq 升序；hasMore 表示还有更早消息（对齐 MessagePage）。</summary>
    public sealed record MessagePageResponse(IReadOnlyList<Message> Messages, bool HasMore);
}
