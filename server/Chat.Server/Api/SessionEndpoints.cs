using System.Security.Claims;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Auth;
using Chat.Server.Realtime;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 登录会话 REST 边界（F-13）：GET /api/sessions 列出当前用户的登录设备，
/// DELETE /api/sessions/{sessionId} 软撤销指定会话并踢掉该会话的 WS 连接。
/// 不提供自动撤销其他会话/踢当前设备之外的额外语义（计划 §F-13）。
/// </summary>
public static class SessionEndpoints
{
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/sessions", ListSessionsAsync)
            .WithName("ListSessions")
            .RequireAuthorization()
            .Produces<IReadOnlyList<SessionInfoDto>>();

        app.MapDelete("/api/sessions/{sessionId}", RevokeSessionAsync)
            .WithName("RevokeSession")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);

        return app;
    }

    private static async Task<IResult> ListSessionsAsync(
        ClaimsPrincipal principal,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var currentJwtId = principal.JwtIdOrNull();
        var sessions = await auth.ListSessionsAsync(userId, currentJwtId, cancellationToken);
        return Results.Json(sessions);
    }

    private static async Task<IResult> RevokeSessionAsync(
        ClaimsPrincipal principal,
        string sessionId,
        AuthService auth,
        ConnectionManager connections,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        await auth.RevokeSessionAsync(userId, sessionId, cancellationToken);

        // 撤销成功后踢掉仍在线的该会话连接（前端走既有 kicked 清理，不自动重连）。
        foreach (var connection in connections.GetConnections(userId))
        {
            if (!string.Equals(connection.SessionId, sessionId, StringComparison.Ordinal))
            {
                continue;
            }

            connections.Unregister(connection);
            try
            {
                await connection.KickAsync("该设备已被退出登录", cancellationToken);
            }
            catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException)
            {
                // 连接已断开/请求已取消，不影响撤销结果。
            }
        }

        return Results.NoContent();
    }
}
