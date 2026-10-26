using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Friends;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace Chat.Server.Api;

/// <summary>
/// 好友与好友申请 REST 端点（F-17，protocol/protocol.md §5.2 复用 REST 错误码）：
/// 好友列表/申请列表、发起申请、接受/拒绝/撤销申请、修改备注分组、删除好友。
/// 变更后通过 <see cref="FriendRealtimeNotifier"/> 给相关在线用户推送 friend.changed。
/// </summary>
public static class FriendEndpoints
{
    public static void MapFriendEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/friends").RequireAuthorization();
        group.MapGet("/", ListFriendsAsync);
        group.MapGet("/requests", ListRequestsAsync);
        group.MapPost("/requests", SendRequestAsync);
        group.MapPost("/requests/{requestId}/accept", AcceptAsync);
        group.MapPost("/requests/{requestId}/reject", RejectAsync);
        group.MapDelete("/requests/{requestId}", CancelAsync);
        group.MapPatch("/{friendUserId}", UpdateFriendAsync);
        group.MapDelete("/{friendUserId}", DeleteFriendAsync);
    }

    private static async Task<IResult> ListFriendsAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var result = await friends.ListAsync(userId, cancellationToken);
        return Results.Json(new FriendsResponse(result));
    }

    private static async Task<IResult> ListRequestsAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var result = await friends.ListRequestsAsync(userId, cancellationToken);
        return Results.Json(new FriendRequestsResponse(result.Incoming, result.Outgoing));
    }

    private static async Task<IResult> SendRequestAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        [FromBody] CreateFriendRequestRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var result = await friends.SendRequestAsync(userId, request.ToUserId, request.Message, cancellationToken);

        if (result.AutoAccepted && result.Friend is not null)
        {
            // 反向 pending 被自动接受：原申请人（对方）收到 request.accepted + friend.added，
            // 我收到 friend.added（双方视角各自翻转）。
            var applicantView = await friends.GetFriendViewAsync(result.Friend.UserId, userId, cancellationToken);
            notifier.PushRequestAccepted(result.Friend.UserId, result.Request, applicantView);
            notifier.PushFriendAdded(result.Friend.UserId, applicantView);
            notifier.PushFriendAdded(userId, result.Friend);
        }
        else
        {
            notifier.PushRequestReceived(result.Request.ToUserId, result.Request);
        }

        return Results.Json(
            new SendFriendRequestResponse(result.Request, result.AutoAccepted, result.Friend),
            statusCode: StatusCodes.Status201Created);
    }

    private static async Task<IResult> AcceptAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        string requestId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var result = await friends.AcceptAsync(userId, requestId, cancellationToken);

        // result.Friend 为「我（接受方）」的视角（friend.UserId = 申请人）。
        var applicantView = await friends.GetFriendViewAsync(result.Friend.UserId, userId, cancellationToken);
        notifier.PushFriendAdded(userId, result.Friend);
        notifier.PushFriendAdded(result.Friend.UserId, applicantView);
        notifier.PushRequestAccepted(result.Friend.UserId, result.Request, applicantView);
        return Results.Json(new FriendResponse(result.Friend));
    }

    private static async Task<IResult> RejectAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        string requestId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var request = await friends.RejectAsync(userId, requestId, cancellationToken);
        notifier.PushRequestRejected(request.FromUserId, request);
        return Results.NoContent();
    }

    private static async Task<IResult> CancelAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        string requestId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var request = await friends.CancelAsync(userId, requestId, cancellationToken);
        notifier.PushRequestCancelled(request.ToUserId, request);
        return Results.NoContent();
    }

    private static async Task<IResult> UpdateFriendAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        string friendUserId,
        [FromBody] UpdateFriendRequest request,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var friend = await friends.UpdateFriendAsync(
            userId, friendUserId, request.Remark, request.GroupName, cancellationToken);
        notifier.PushFriendUpdated(userId, friend);
        return Results.Json(new FriendResponse(friend));
    }

    private static async Task<IResult> DeleteFriendAsync(
        ClaimsPrincipal principal,
        FriendService friends,
        FriendRealtimeNotifier notifier,
        string friendUserId,
        CancellationToken cancellationToken)
    {
        var userId = GetUserId(principal);
        var result = await friends.RemoveAsync(userId, friendUserId, cancellationToken);

        // 双向删除：双方各收到自己视角的 friend.removed。
        notifier.PushFriendRemoved(userId, result.Own);
        notifier.PushFriendRemoved(friendUserId, result.Other);
        return Results.NoContent();
    }

    private static string GetUserId(ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value ??
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        throw ApiException.Unauthorized("未登录");

    public sealed record FriendsResponse(IReadOnlyList<Friend> Friends);

    public sealed record FriendResponse(Friend Friend);

    public sealed record FriendRequestsResponse(
        IReadOnlyList<FriendRequest> Incoming,
        IReadOnlyList<FriendRequest> Outgoing);

    public sealed record CreateFriendRequestRequest(string? ToUserId, string? Message);

    public sealed record SendFriendRequestResponse(FriendRequest Request, bool AutoAccepted, Friend? Friend);

    public sealed record UpdateFriendRequest(string? Remark, string? GroupName);
}
