using System.Security.Claims;
using System.Text.Json.Serialization;
using Chat.Server.Common.Errors;
using Chat.Server.Modules.Auth;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>用户 REST 边界（计划 §7）：M1-05 起提供 GET /api/me；搜索等留 M2。</summary>
public static class UserEndpoints
{
    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", GetMeAsync)
            .WithName("GetMe")
            .RequireAuthorization()
            .Produces<MeResponse>();

        return app;
    }

    private static async Task<IResult> GetMeAsync(
        ClaimsPrincipal principal,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");
        var profile = await auth.GetProfileAsync(userId, cancellationToken);
        return Results.Json(new MeResponse(
            profile.UserId,
            profile.Email,
            profile.DisplayName,
            profile.AvatarFileId,
            profile.Role,
            profile.Status,
            profile.CreatedAt));
    }

    /// <summary>当前用户资料（字段与 apps/web/src/api/users.ts 的 UserProfile 对齐）。</summary>
    public sealed record MeResponse(
        string UserId,
        string Email,
        string DisplayName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarFileId,
        string Role,
        string Status,
        long CreatedAt);
}
