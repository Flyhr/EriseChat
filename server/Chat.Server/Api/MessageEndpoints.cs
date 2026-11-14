using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Messages;
using Chat.Server.Realtime;
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
        // F-34：全局搜索增加 (beforeCreatedAt, beforeMessageId) 复合游标；会话内仍用 beforeSeq。
        app.MapGet("/api/messages/search", SearchAsync)
            .WithName("SearchMessages")
            .RequireAuthorization()
            .Produces<MessageSearchPageResponse>();

        // F-36：按目标消息定位上下文（置顶/搜索跳转超出缓存范围时使用），只读历史、不推进同步游标。
        app.MapGet("/api/conversations/{conversationId}/messages/context", GetContextAsync)
            .WithName("GetMessageContext")
            .RequireAuthorization()
            .Produces<MessageContextResponse>();

        // F-40：单条转发（text/image/file/voice）。附件复用原 files 记录，不重新上传；
        // 每条转发由客户端提供稳定 clientMsgId，服务端按目标会话幂等。
        app.MapPost("/api/messages/forward", ForwardAsync)
            .WithName("ForwardMessage")
            .RequireAuthorization()
            .Produces<Message>();

        // C3：设置/移除我的表情回应（显式 PUT/DELETE，均幂等；界面上的「切换」由前端转换成两种请求）。
        // 其中 PUT 重复相同表情、DELETE 重复移除都返回当前摘要且不重复广播。
        app.MapPut("/api/conversations/{conversationId}/messages/{messageId}/reactions/mine", SetReactionAsync)
            .WithName("SetMessageReaction")
            .RequireAuthorization()
            .Produces<MessageReactionsResponse>();

        app.MapDelete("/api/conversations/{conversationId}/messages/{messageId}/reactions/mine", RemoveReactionAsync)
            .WithName("RemoveMessageReaction")
            .RequireAuthorization()
            .Produces<MessageReactionsResponse>();

        return app;
    }

    /// <summary>C3：设置回应请求体；reaction 为稳定表情标识（服务端白名单校验）。</summary>
    public sealed record SetReactionRequest(string? Reaction);

    /// <summary>C3：当前账号视角的消息回应摘要（空数组表示该消息没有任何回应）。</summary>
    public sealed record MessageReactionsResponse(IReadOnlyList<ReactionSummary> Reactions);

    private static async Task<IResult> SetReactionAsync(
        string conversationId,
        string messageId,
        SetReactionRequest request,
        ClaimsPrincipal principal,
        MessageService messages,
        MessageHub hub,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var result = await messages.SetReactionAsync(
            userId,
            conversationId,
            messageId,
            request.Reaction,
            cancellationToken);
        if (result.Changed)
        {
            await hub.BroadcastReactionsAsync(result);
        }

        return Results.Json(new MessageReactionsResponse(result.SummariesByUser[userId]));
    }

    private static async Task<IResult> RemoveReactionAsync(
        string conversationId,
        string messageId,
        ClaimsPrincipal principal,
        MessageService messages,
        MessageHub hub,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var result = await messages.RemoveReactionAsync(userId, conversationId, messageId, cancellationToken);
        if (result.Changed)
        {
            await hub.BroadcastReactionsAsync(result);
        }

        return Results.Json(new MessageReactionsResponse(result.SummariesByUser[userId]));
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
        long? beforeCreatedAt,
        string? beforeMessageId,
        string? senderId,
        long? from,
        long? to,
        bool? timeCursor,
        bool? linkOnly,
        int? limit,
        ClaimsPrincipal principal,
        MessageService messages,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var page = await messages.SearchAsync(
            userId,
            q,
            conversationId,
            beforeSeq,
            limit,
            cancellationToken,
            beforeCreatedAt,
            beforeMessageId,
            senderId,
            from,
            to,
            timeCursor ?? false,
            linkOnly ?? false);
        return Results.Json(new MessageSearchPageResponse(
            page.Messages,
            page.HasMore,
            page.NextCursor is null
                ? null
                : new MessageSearchCursorResponse(page.NextCursor.CreatedAt, page.NextCursor.MessageId)));
    }

    private static async Task<IResult> GetContextAsync(
        string conversationId,
        string? messageId,
        long? seq,
        int? before,
        int? after,
        ClaimsPrincipal principal,
        MessageService messages,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var result = await messages.GetMessageContextAsync(
            userId,
            conversationId,
            messageId,
            seq,
            before,
            after,
            cancellationToken);
        return Results.Json(new MessageContextResponse(result.Messages, result.HasOlder, result.HasNewer));
    }

    private static async Task<IResult> ForwardAsync(
        ForwardMessageRequest request,
        ClaimsPrincipal principal,
        MessageService messages,
        MessageHub hub,
        CancellationToken cancellationToken)
    {
        var result = await messages.ForwardAsync(
            principal.UserId(),
            request.MessageId,
            request.TargetConversationId,
            request.ClientMsgId,
            cancellationToken);

        // 幂等命中（重试/并发）不重复广播，返回已存在消息，避免接收端重复处理。
        if (result.Created)
        {
            await hub.BroadcastForwardedAsync(result.MemberUserIds, result.Message);
        }

        return Results.Json(result.Message);
    }

    /// <summary>F-40：转发请求；messageId/targetConversationId/clientMsgId 必填，clientMsgId 用于重试幂等。</summary>
    public sealed record ForwardMessageRequest(
        string? MessageId,
        string? TargetConversationId,
        string? ClientMsgId);

    /// <summary>F-36：按目标消息定位的上下文窗口响应（非协议帧）。</summary>
    public sealed record MessageContextResponse(
        IReadOnlyList<Message> Messages,
        bool HasOlder,
        bool HasNewer);

    /// <summary>
    /// 搜索响应（F-11/F-34，非协议帧）：会话内按 seq 降序，全局按 createdAt/id 倒序；
    /// hasMore 表示可继续翻页；nextCursor 仅全局搜索返回（下次请求的 beforeCreatedAt/beforeMessageId）。
    /// </summary>
    public sealed record MessageSearchPageResponse(
        IReadOnlyList<Message> Messages,
        bool HasMore,
        MessageSearchCursorResponse? NextCursor);

    /// <summary>全局搜索翻页游标（F-34）。</summary>
    public sealed record MessageSearchCursorResponse(long CreatedAt, string MessageId);
}
