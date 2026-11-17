using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Chat.Server.Common.Options;
using Chat.Server.Common.Security;
using Chat.Server.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

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
        group.MapGet("/ice-servers", IceServers);
        group.MapGet("/can-call/{conversationId}", CanCallAsync);
    }

    /// <summary>
    /// 当前用户能否在此单聊发起通话（客户端据此置灰通话按钮）。
    /// 条件：好友直接放行；非好友要求双方都发过消息。
    /// </summary>
    private static async Task<IResult> CanCallAsync(
        string conversationId,
        ClaimsPrincipal principal,
        Chat.Server.Modules.Calls.CallSessionManager calls,
        CancellationToken cancellationToken)
    {
        var allowed = await calls.CanCallAsync(principal.UserId(), conversationId, cancellationToken);
        return Results.Json(new CanCallResponse(allowed));
    }

    /// <summary>
    /// 下发带短期凭证的 ICE 服务器（protocol.md §3.1）：用户名是 <c>过期时间:用户 ID</c>，
    /// 凭证是 <c>base64(HMAC-SHA1(密钥, 用户名))</c>，与 coturn 的 <c>static-auth-secret</c> 配套。
    /// 未配置 TURN 时返回空列表，客户端退回纯 P2P。
    /// </summary>
    private static IResult IceServers(ClaimsPrincipal principal, IOptions<CallOptions> options)
    {
        var call = options.Value;
        if (call.TurnUrls.Length == 0 || string.IsNullOrWhiteSpace(call.TurnSharedSecret))
        {
            return Results.Json(new IceServersResponse([]));
        }

        var ttl = Math.Clamp(call.TurnCredentialTtlSeconds, 60, 86_400);
        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(ttl).ToUnixTimeSeconds();
        var username = $"{expiresAt}:{principal.UserId()}";
        var hash = new HMACSHA1(Encoding.UTF8.GetBytes(call.TurnSharedSecret)).ComputeHash(Encoding.UTF8.GetBytes(username));
        var credential = Convert.ToBase64String(hash);

        return Results.Json(new IceServersResponse([new IceServerDto(call.TurnUrls, username, credential)]));
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

internal sealed record CanCallResponse(bool Allowed);

internal sealed record IceServerDto(IReadOnlyList<string> Urls, string Username, string Credential);

internal sealed record IceServersResponse(IReadOnlyList<IceServerDto> IceServers);
