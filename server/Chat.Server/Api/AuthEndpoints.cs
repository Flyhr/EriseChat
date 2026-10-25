using System.Security.Claims;
using System.Text.Json.Serialization;
using Chat.Server.Common.Errors;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 认证 REST 边界（计划 §4.1 / §7）：发送验证码、注册、验证码登录、密码登录、注销。
/// Api 层只做参数绑定/校验与 DTO 映射，业务规则在 Modules/Auth。
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        group.MapPost("/email/send", SendEmailCodeAsync)
            .WithName("SendEmailCode")
            .Produces<SendEmailCodeResponse>();

        group.MapPost("/register", RegisterAsync)
            .WithName("Register")
            .Produces<AuthSessionResponse>();

        group.MapPost("/login/code", LoginWithCodeAsync)
            .WithName("LoginWithCode")
            .Produces<AuthSessionResponse>();

        group.MapPost("/login/password", LoginWithPasswordAsync)
            .WithName("LoginWithPassword")
            .Produces<AuthSessionResponse>();

        group.MapPost("/logout", LogoutAsync)
            .WithName("Logout")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> SendEmailCodeAsync(
        SendEmailCodeRequest request,
        HttpContext context,
        EmailCodeService emailCodes,
        CancellationToken cancellationToken)
    {
        var purpose = ParsePurpose(request.Purpose);
        var result = await emailCodes.SendAsync(request.Email, purpose, GetClientIp(context), cancellationToken);
        return Results.Json(new SendEmailCodeResponse(result.CooldownSec, result.DevCode));
    }

    private static async Task<IResult> RegisterAsync(
        RegisterRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var result = await auth.RegisterAsync(
            request.Email,
            request.Code,
            request.DisplayName,
            request.Password,
            request.DeviceId,
            cancellationToken);
        return Results.Json(ToResponse(result));
    }

    private static async Task<IResult> LoginWithCodeAsync(
        LoginWithCodeRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var result = await auth.LoginWithCodeAsync(request.Email, request.Code, request.DeviceId, cancellationToken);
        return Results.Json(ToResponse(result));
    }

    private static async Task<IResult> LoginWithPasswordAsync(
        LoginWithPasswordRequest request,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var result = await auth.LoginWithPasswordAsync(request.Email, request.Password, request.DeviceId, cancellationToken);
        return Results.Json(ToResponse(result));
    }

    private static async Task<IResult> LogoutAsync(
        ClaimsPrincipal principal,
        AuthService auth,
        CancellationToken cancellationToken)
    {
        var userId = principal.FindFirst("sub")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");
        var jwtId = principal.FindFirst("jti")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");
        await auth.LogoutAsync(userId, jwtId, cancellationToken);
        return Results.NoContent();
    }

    private static EmailCodePurpose ParsePurpose(string? rawPurpose) => rawPurpose?.Trim().ToLowerInvariant() switch
    {
        "register" => EmailCodePurpose.Register,
        "login" => EmailCodePurpose.Login,
        _ => throw ApiException.ValidationFailed("purpose 必须是 register 或 login"),
    };

    private static AuthSessionResponse ToResponse(AuthSessionResult result) => new(
        result.AccessToken,
        result.ExpiresAt,
        new AuthUserResponse(result.User.UserId, result.User.Email, result.User.DisplayName, result.User.AvatarFileId, result.User.Role));

    /// <summary>取客户端 IP（IPv4-mapped IPv6 归一为 IPv4）；仅用于限流与排查，不信任代理头。</summary>
    private static string? GetClientIp(HttpContext context)
    {
        var address = context.Connection.RemoteIpAddress;
        if (address is null)
        {
            return null;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        return address.ToString();
    }

    /// <summary>发送验证码请求（字段小写 camelCase，与 apps/web 的 SendEmailCodeRequest 对齐）。</summary>
    public sealed record SendEmailCodeRequest(string? Email, string? Purpose);

    /// <summary>发送验证码响应：devCode 仅 Development 返回，且为 null 时不输出该字段。</summary>
    public sealed record SendEmailCodeResponse(
        int CooldownSec,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DevCode);

    /// <summary>注册请求；deviceId 为可选扩展字段（缺省时服务端生成，登录接口必须显式传）。</summary>
    public sealed record RegisterRequest(string? Email, string? Code, string? DisplayName, string? Password, string? DeviceId);

    /// <summary>验证码登录请求。</summary>
    public sealed record LoginWithCodeRequest(string? Email, string? Code, string? DeviceId);

    /// <summary>密码登录请求。</summary>
    public sealed record LoginWithPasswordRequest(string? Email, string? Password, string? DeviceId);

    /// <summary>用户信息（与 apps/web/src/api/auth.ts 的 AuthUser 对齐；avatarFileId 为 null 时不输出）。</summary>
    public sealed record AuthUserResponse(
        string UserId,
        string Email,
        string DisplayName,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AvatarFileId,
        string Role);

    /// <summary>登录/注册响应（与 apps/web/src/api/auth.ts 的 LoginResponse 对齐）。</summary>
    public sealed record AuthSessionResponse(string AccessToken, long ExpiresAt, AuthUserResponse User);
}
