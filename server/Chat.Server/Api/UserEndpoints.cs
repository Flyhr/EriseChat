using System.Security.Claims;
using System.Text.Json.Serialization;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
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

        app.MapPatch("/api/me/profile", UpdateMeAsync)
            .WithName("UpdateMeProfile")
            .RequireAuthorization()
            .Produces<MeResponse>();

        app.MapPost("/api/me/password", ChangePasswordAsync)
            .WithName("ChangePassword")
            .RequireAuthorization()
            .Produces(StatusCodes.Status204NoContent);

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
        var userId = principal.UserId();
        var profile = await auth.GetProfileAsync(userId, cancellationToken);
        return Results.Json(new MeResponse(
            profile.UserId,
            profile.Email,
            profile.DisplayName,
            profile.AvatarFileId,
            profile.Role,
            profile.Status,
            profile.CreatedAt,
            profile.HasPassword));
    }

    /// <summary>F-13：修改/设置密码（已有密码须校验旧密码；从未设置过密码时直接设置）。</summary>
    private static async Task<IResult> ChangePasswordAsync(
        ClaimsPrincipal principal,
        ChangePasswordRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        await auth.ChangePasswordAsync(userId, request.OldPassword, request.NewPassword ?? string.Empty, cancellationToken);
        return Results.NoContent();
    }

    /// <summary>F-12：修改当前用户昵称与头像引用（计划 §F-12）；两项至少一项，null 表示不修改。</summary>
    private static async Task<IResult> UpdateMeAsync(
        ClaimsPrincipal principal,
        UpdateMeRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var userId = principal.UserId();
        var profile = await auth.UpdateProfileAsync(userId, request.DisplayName, request.AvatarFileId, cancellationToken);
        return Results.Json(new MeResponse(
            profile.UserId,
            profile.Email,
            profile.DisplayName,
            profile.AvatarFileId,
            profile.Role,
            profile.Status,
            profile.CreatedAt,
            profile.HasPassword));
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
        var currentUserId = principal.UserId();

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

        // F-17：附带与当前用户的关系（friend/outgoing/incoming），供联系人页决定「加为好友」等操作。
        var userIds = rows.Select(user => user.Id).ToList();
        var friendIds = (await db.Friends.AsNoTracking()
                .Where(friend => friend.OwnerUserId == currentUserId && userIds.Contains(friend.FriendUserId))
                .Select(friend => friend.FriendUserId)
                .ToListAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var pendingRequests = await db.FriendRequests.AsNoTracking()
            .Where(request => request.Status == Chat.Protocol.FriendRequestStatus.Pending &&
                             (request.FromUserId == currentUserId || request.ToUserId == currentUserId) &&
                             (userIds.Contains(request.FromUserId) || userIds.Contains(request.ToUserId)))
            .ToListAsync(cancellationToken);
        var outgoingIds = pendingRequests
            .Where(request => request.FromUserId == currentUserId)
            .Select(request => request.ToUserId)
            .ToHashSet(StringComparer.Ordinal);
        var incomingIds = pendingRequests
            .Where(request => request.ToUserId == currentUserId)
            .Select(request => request.FromUserId)
            .ToHashSet(StringComparer.Ordinal);

        var users = rows.Select(user => ToResponse(
            user,
            friendIds.Contains(user.Id) ? "friend"
            : outgoingIds.Contains(user.Id) ? "outgoing"
            : incomingIds.Contains(user.Id) ? "incoming"
            : null)).ToList();
        return Results.Json(new UsersSearchResponse(users));
    }

    private static MeResponse ToResponse(User user, string? relation = null) => new(
        user.Id,
        user.Email,
        user.DisplayName,
        user.AvatarFileId,
        user.Role.ToString().ToLowerInvariant(),
        user.Status.ToString().ToLowerInvariant(),
        user.CreatedAt,
        user.PasswordHash is not null,
        relation);

    /// <summary>SQLite LIKE 通配符转义（配合 EF.Functions.Like 的 escape 参数 '\'）。</summary>
    private static string EscapeLikePattern(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);

    /// <summary>F-12：PATCH /api/me/profile 请求体（两项至少一项；null 表示不修改，v1 不支持清除头像）。</summary>
    public sealed record UpdateMeRequest(string? DisplayName = null, string? AvatarFileId = null);

    /// <summary>F-13：POST /api/me/password 请求体（已有密码须 oldPassword；从未设置过密码可省略）。</summary>
    public sealed record ChangePasswordRequest(string? OldPassword = null, string? NewPassword = null);

    /// <summary>当前用户资料（字段与 apps/web/src/api/users.ts 的 UserProfile 对齐）。</summary>
    public sealed record MeResponse(
        string UserId,
        string Email,
        string DisplayName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarFileId,
        string Role,
        string Status,
        long CreatedAt,
        /// <summary>F-13：是否已设置密码（前端据此显示「修改密码」或「设置密码」）。</summary>
        bool HasPassword = false,
        /// <summary>F-17：与当前用户的关系（friend/outgoing/incoming），仅 /api/users 搜索时返回。</summary>
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Relation = null);

    /// <summary>用户搜索结果（字段与 apps/web/src/api/users.ts 的 SearchUsersResponse 对齐）。</summary>
    public sealed record UsersSearchResponse(IReadOnlyList<MeResponse> Users);
}
