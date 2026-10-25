using Microsoft.Extensions.Logging;

namespace Chat.Server.Realtime;

/// <summary>
/// /ws 路由中间件（M1-06）：仅处理 GET /ws 的 WebSocket 升级请求，其它请求原样放行给后续中间件。
/// 升级后交给 WebSocketConnectionHandler 完成认证/心跳/帧循环。
/// </summary>
public sealed class WsMiddleware
{
    /// <summary>WebSocket 端点路径（protocol/protocol.md §1）。</summary>
    public const string Path = "/ws";

    private readonly RequestDelegate _next;
    private readonly ILogger<WsMiddleware> _logger;

    public WsMiddleware(RequestDelegate next, ILogger<WsMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, WebSocketConnectionHandler handler)
    {
        if (!HttpMethods.IsGet(context.Request.Method) ||
            !context.Request.Path.Equals(Path, StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        if (!context.WebSockets.IsWebSocketRequest)
        {
            _logger.LogDebug("非 WebSocket 请求访问 {Path}，返回 400", Path);
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        await handler.HandleAsync(context, socket);
    }
}
