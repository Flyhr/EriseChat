using System.Security.Claims;
using System.Text.Json.Serialization;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Chat.Server.Api;

/// <summary>用户 REST 边界（计划 §7）：GET /api/me（M1-05）、GET /api/users?search=（M2-01）。</summary>
public static class UserEndpoints
{
    private const int SearchMaxLength = 64;
    private const int SearchLimit = 20;

    public static IEndpointRouteBuilder MapUserEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/me", GetMeAsync)
            .WithName("GetMe")
            .RequireAuthorization()
            .Produces<MeResponse>();

        app.MapGet("/api/users", SearchUsersAsync)
            .WithName("SearchUsers")
            .RequireAuthorization()
            .Produces<UsersSearchResponse>();

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

    /// <summary>
    /// 按邮箱/昵称模糊搜索用户（M2-01，计划 §7）：search 去空白后 1~64 字符；
    /// LIKE 转义 % / _ 为字面量；排除当前用户；按昵称排序；最多 20 条；无匹配返回空数组。
    /// </summary>
    private static async Task<IResult> SearchUsersAsync(
        string? search,
        ClaimsPrincipal principal,
        ChatDbContext db,
        CancellationToken cancellationToken)
    {
        var currentUserId = principal.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");

        var keyword = (search ?? string.Empty).Trim();
        if (keyword.Length is < 1 or > SearchMaxLength)
        {
            throw ApiException.ValidationFailed($"search 长度应为 1~{SearchMaxLength} 个字符");
        }

        var pattern = $"%{EscapeLikePattern(keyword)}%";
        var rows = await db.Users.AsNoTracking()
            .Where(user => user.Id != currentUserId)
            .Where(user => EF.Functions.Like(user.Email, pattern, "\\") ||
                           EF.Functions.Like(user.DisplayName, pattern, "\\"))
            .OrderBy(user => user.DisplayName)
            .Take(SearchLimit)
            .ToListAsync(cancellationToken);

        var users = rows.Select(ToResponse).ToList();
        return Results.Json(new UsersSearchResponse(users));
    }

    private static MeResponse ToResponse(User user) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.AvatarFileId,
        user.Role.ToString().ToLowerInvariant(),
        user.Status.ToString().ToLowerInvariant(),
        user.CreatedAt);

    /// <summary>SQLite LIKE 通配符转义（配合 EF.Functions.Like 的 escape 参数 '\'）。</summary>
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>当前用户资料（字段与 apps/web/src/api/users.ts 的 UserProfile 对齐）。</summary>
    public sealed record MeResponse(
        string UserId,
        string Email,
        string DisplayName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarFileId,
        string Role,
        string Status,
        long CreatedAt);

    /// <summary>用户搜索结果（字段与 apps/web/src/api/users.ts 的 SearchUsersResponse 对齐）。</summary>
    public sealed record UsersSearchResponse(IReadOnlyList<MeResponse> Users);
}
