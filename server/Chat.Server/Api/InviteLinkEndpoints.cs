using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Conversations;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 群邀请链接 REST 边界（C1c）：创建/列出/撤销链接、打开链接预览、兑换加入群聊。
/// 响应对象见 protocol/protocol.md §7 第 16 条；只处理「加入群聊」。
/// </summary>
public static class InviteLinkEndpoints
{
    public static IEndpointRouteBuilder MapInviteLinkEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api").RequireAuthorization();

        group.MapPost("/conversations/{conversationId}/invite-links", CreateAsync)
            .WithName("CreateGroupInviteLink")
            .Produces<GroupInviteLink>();

        group.MapGet("/conversations/{conversationId}/invite-links", ListAsync)
            .WithName("ListGroupInviteLinks")
            .Produces<InviteLinksResponse>();

        group.MapDelete("/conversations/{conversationId}/invite-links/{token}", RevokeAsync)
            .WithName("RevokeGroupInviteLink");

        group.MapGet("/invite-links/{token}", PreviewAsync)
            .WithName("PreviewGroupInviteLink")
            .Produces<GroupInvitePreview>();

        group.MapPost("/invite-links/{token}/join", JoinAsync)
            .WithName("JoinGroupByInviteLink")
            .Produces<Conversation>();

        return app;
    }

    /// <summary>创建群邀请链接（群成员；GroupOnlyOwnerCanInvite 时仅群主）。</summary>
    private static async Task<IResult> CreateAsync(
        ClaimsPrincipal principal,
        string conversationId,
        GroupInviteService invites,
        CancellationToken cancellationToken)
    {
        var link = await invites.CreateAsync(principal.UserId(), conversationId, cancellationToken);
        return Results.Json(link);
    }

    /// <summary>列出未撤销的群邀请链接（群成员；GroupOnlyOwnerCanInvite 时仅群主）。</summary>
    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        string conversationId,
        GroupInviteService invites,
        CancellationToken cancellationToken)
    {
        var links = await invites.ListAsync(principal.UserId(), conversationId, cancellationToken);
        return Results.Json(new InviteLinksResponse(links));
    }

    /// <summary>撤销群邀请链接（群主或创建者）；成功 204，重复撤销幂等。</summary>
    private static async Task<IResult> RevokeAsync(
        ClaimsPrincipal principal,
        string conversationId,
        string token,
        GroupInviteService invites,
        CancellationToken cancellationToken)
    {
        await invites.RevokeAsync(principal.UserId(), conversationId, token, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>打开链接的预览：目标群、成员数、是否过期/撤销、是否已在群内。</summary>
    private static async Task<IResult> PreviewAsync(
        ClaimsPrincipal principal,
        string token,
        GroupInviteService invites,
        CancellationToken cancellationToken)
    {
        var preview = await invites.PreviewAsync(principal.UserId(), token, cancellationToken);
        return Results.Json(preview);
    }

    /// <summary>兑换链接加入群聊：成功返回当前用户视角的会话；重复/并发兑换幂等。</summary>
    private static async Task<IResult> JoinAsync(
        ClaimsPrincipal principal,
        string token,
        GroupInviteService invites,
        ConversationRealtimeNotifier notifier,
        CancellationToken cancellationToken)
    {
        var result = await invites.JoinAsync(principal.UserId(), token, cancellationToken);

        // 有变更才推送：message.new（系统消息）先于 conv.updated（含全体成员的最新会话）。
        if (result.SystemMessage is not null)
        {
            notifier.PushNewMessage(result.SystemMessage, result.MemberUserIds);
            notifier.PushUpdated(new[] { result.Conversation });
        }

        return Results.Json(result.Conversation);
    }

    /// <summary>邀请链接列表响应。</summary>
    public sealed record InviteLinksResponse(IReadOnlyList<GroupInviteLink> Links);
}
