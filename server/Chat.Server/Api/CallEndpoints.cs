using System.Security.Claims;
using Chat.Server.Common.Security;
using Chat.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Api;

/// <summary>
/// 通话历史（protocol.md §3.1）：GET /api/calls 返回本人最近 50 条通话记录。
/// 只读投影，不含 SDP / ICE（协议约定不落库）。
/// </summary>
public static class CallEndpoints
{
    /// <summary>单次最多返回的记录数。</summary>
    private const int MaxItems = 50;

    public static void MapCallEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/calls").RequireAuthorization();
        group.MapGet("/", ListAsync);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        ChatDbContext db,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();

        var calls = await db.Calls
            .AsNoTracking()
            .Where(call => call.CallerId == currentUserId || call.CalleeId == currentUserId)
            .OrderByDescending(call => call.CreatedAt)
            .Take(MaxItems)
            .ToListAsync(cancellationToken);

        var peerIds = calls
            .Select(call => call.CallerId == currentUserId ? call.CalleeId : call.CallerId)
            .Distinct()
            .ToArray();

        var peers = (await db.Users
                .AsNoTracking()
                .Where(user => peerIds.Contains(user.Id))
                .Select(user => new CallPeerSummary(user.Id, user.DisplayName, user.AvatarFileId))
                .ToListAsync(cancellationToken))
            .ToDictionary(peer => peer.UserId, StringComparer.Ordinal);

        var items = new List<CallHistoryItem>(calls.Count);
        foreach (var call in calls)
        {
            var peerUserId = call.CallerId == currentUserId ? call.CalleeId : call.CallerId;
            var peer = peers.TryGetValue(peerUserId, out var found)
                ? found
                : new CallPeerSummary(peerUserId, "已注销用户", null);

            long? durationSeconds = call.ConnectedAt is { } connectedAt && call.EndedAt is { } endedAt
                ? Math.Max(0, endedAt - connectedAt) / 1000
                : null;

            items.Add(new CallHistoryItem(
                call.Id,
                call.ConversationId,
                call.Kind.ToString().ToLowerInvariant(),
                call.State.ToString().ToLowerInvariant(),
                call.CallerId == currentUserId ? "outgoing" : "incoming",
                peer,
                call.CreatedAt,
                call.AnsweredAt,
                call.ConnectedAt,
                call.EndedAt,
                call.EndReason,
                durationSeconds));
        }

        return Results.Json(new CallHistoryResponse(items));
    }
}

internal sealed record CallPeerSummary(string UserId, string DisplayName, string? AvatarFileId);

internal sealed record CallHistoryItem(
    string CallId,
    string ConversationId,
    string Kind,
    string State,
    string Direction,
    CallPeerSummary Peer,
    long CreatedAt,
    long? AnsweredAt,
    long? ConnectedAt,
    long? EndedAt,
    string? EndReason,
    long? DurationSeconds);

internal sealed record CallHistoryResponse(IReadOnlyList<CallHistoryItem> Items);
