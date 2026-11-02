using Chat.Server.Common.Options;
using Chat.Server.Modules.Release;
using Microsoft.Extensions.Options;

namespace Chat.Server.Api;

/// <summary>
/// 客户端更新检查（F-29，protocol/protocol.md §7 第 10 条）：
/// <c>GET /api/version</c> 匿名返回 <c>{ version, protocolVersion, artifacts: { desktop, android } }</c>，
/// 产物地址固定为同源 <c>/download/files/{fileName}</c>，sha256 供客户端下载后校验。
/// </summary>
public static class VersionEndpoints
{
    public static IEndpointRouteBuilder MapVersionEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/version",
                async (ReleaseArtifactService releases, IOptions<AppOptions> options, CancellationToken cancellationToken) =>
                    Results.Ok(await releases.GetManifestAsync(options.Value.ProtocolVersion, cancellationToken)))
            .WithName("VersionManifest")
            .AllowAnonymous();

        return app;
    }
}
