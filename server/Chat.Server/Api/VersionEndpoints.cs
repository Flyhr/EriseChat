using Chat.Server.Common.Options;
using Chat.Server.Modules.Release;
using Microsoft.Extensions.Options;

namespace Chat.Server.Api;

/// <summary>
/// 客户端更新检查（F-29，protocol/protocol.md §7 第 10 条）：
/// <c>GET /api/version</c> 匿名返回 <c>{ version, protocolVersion, artifacts: { desktop, android }, webVersion, minVersion, releaseNotes, publishedAt }</c>，
/// 产物地址固定为同源 <c>/download/files/{fileName}</c>，sha256 供客户端下载后校验。
/// 2026-11-12：清单必须实时，响应加 <c>Cache-Control: no-store</c>。
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/version",
                async (HttpContext context, ReleaseArtifactService releases, IOptions<AppOptions> options, CancellationToken cancellationToken) =>
                {
                    context.Response.Headers.CacheControl = "no-store";
                    return Results.Ok(await releases.GetManifestAsync(options.Value.ProtocolVersion, cancellationToken));
                })
            .WithName("VersionManifest")
            .AllowAnonymous();

        return app;
    }
}
