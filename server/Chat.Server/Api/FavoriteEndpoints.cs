using System.Security.Claims;
using Chat.Protocol.Frames;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Favorites;
using Chat.Server.Realtime;

namespace Chat.Server.Api;

/// <summary>
/// 个人收藏 REST（F-38，protocol/protocol.md §7 第 13 条）：
/// GET 列表 / POST 新增（幂等）/ DELETE 取消（幂等）；变更后向本人全部在线连接推送 favorite.changed。
/// </summary>
public static class FavoriteEndpoints
{
    public static IEndpointRouteBuilder MapFavoriteEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/favorites", ListAsync)
            .WithName("ListFavorites")
            .RequireAuthorization()
            .Produces<IReadOnlyList<FavoriteItemDto>>();

        app.MapPost("/api/favorites", AddAsync)
            .WithName("AddFavorite")
            .RequireAuthorization()
            .Produces<FavoriteItemDto>();

        app.MapDelete("/api/favorites/{messageId}", RemoveAsync)
            .WithName("RemoveFavorite")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> ListAsync(
        ClaimsPrincipal principal,
        FavoritesService favorites,
        CancellationToken cancellationToken)
    {
        var items = await favorites.ListAsync(principal.UserId(), cancellationToken);
        return Results.Json(items);
    }

    private static async Task<IResult> AddAsync(
        ClaimsPrincipal principal,
        FavoriteAddRequest request,
        FavoritesService favorites,
        ConnectionManager connections,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var item = await favorites.AddAsync(userId, request.MessageId, cancellationToken);
        await PushChangedAsync(connections, loggerFactory, userId, item.MessageId, favorited: true);
        return Results.Json(item);
    }

    private static async Task<IResult> RemoveAsync(
        string messageId,
        ClaimsPrincipal principal,
        FavoritesService favorites,
        ConnectionManager connections,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        await favorites.RemoveAsync(userId, messageId, cancellationToken);
        await PushChangedAsync(connections, loggerFactory, userId, messageId.Trim(), favorited: false);
        return Results.NoContent();
    }

    private static async Task PushChangedAsync(
        ConnectionManager connections,
        ILoggerFactory loggerFactory,
        string userId,
        string messageId,
        bool favorited)
    {
        var logger = loggerFactory.CreateLogger("Chat.Server.Api.FavoriteEndpoints");
        var frame = new FavoriteChangedFrame(messageId, favorited, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        foreach (var connection in connections.GetConnections(userId))
        {
            try
            {
                await connection.SendFrameAsync(frame, CancellationToken.None);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "favorite.changed 推送失败：用户 {UserId} 连接 {ConnectionId}", userId, connection.ConnectionId);
            }
        }
    }
}

/// <summary>新增收藏请求体（protocol §7 第 13 条）。</summary>
public sealed record FavoriteAddRequest(string? MessageId = null);
