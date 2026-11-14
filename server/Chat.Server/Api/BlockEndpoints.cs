using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Blocks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Chat.Server.Api;

/// <summary>
/// 拉黑关系 REST 端点（C1b，protocol/protocol.md §7 第 15 条）：列表、拉黑、解除拉黑。
/// 拉黑/解除均幂等；变更不推送 WS 帧，客户端在打开资料卡、设置页时重新拉取。
/// </summary>
public static class BlockEndpoints
{
    public static void MapBlockEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/blocks").RequireAuthorization();
        group.MapGet("/", ListAsync);
        group.MapPut("/{userId}", BlockAsync);
        group.MapDelete("/{userId}", UnblockAsync);
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        BlockService blocks,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var result = await blocks.ListAsync(currentUserId, cancellationToken);
        return Results.Json(new BlocksResponse(result));
    }

    private static async Task<IResult> BlockAsync(
        ClaimsPrincipal principal,
        BlockService blocks,
        string userId,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        var blocked = await blocks.BlockAsync(currentUserId, userId, cancellationToken);
        return Results.Json(new BlockResponse(blocked));
    }

    private static async Task<IResult> UnblockAsync(
        ClaimsPrincipal principal,
        BlockService blocks,
        string userId,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.UserId();
        await blocks.UnblockAsync(currentUserId, userId, cancellationToken);
        return Results.NoContent();
    }

    public sealed record BlocksResponse(IReadOnlyList<BlockedUser> Blocks);

    public sealed record BlockResponse(BlockedUser Block);
}
