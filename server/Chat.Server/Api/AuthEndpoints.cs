using System.Text.Json.Serialization;
using Chat.Server.Common.Errors;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 认证 REST 边界（M1-04 起逐步接入；路由见计划 §7）：本批只实现发送邮箱验证码。
/// Api 层只做参数绑定/校验与 DTO 映射，业务规则在 Modules/Auth。
/// </summary>
public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Auth");

        // POST /api/auth/email/send { email, purpose: register|login } → { cooldownSec, devCode? }
        group.MapPost("/email/send", SendEmailCodeAsync)
            .WithName("SendEmailCode")
            .Produces<SendEmailCodeResponse>();

        return app;
    }

    private static async Task<IResult> SendEmailCodeAsync(
        SendEmailCodeRequest request,
        HttpContext context,
        EmailCodeService emailCodes,
        CancellationToken cancellationToken)
    {
        var purpose = request.Purpose?.Trim().ToLowerInvariant() switch
        {
            "register" => EmailCodePurpose.Register,
            "login" => EmailCodePurpose.Login,
            _ => throw ApiException.ValidationFailed("purpose 必须是 register 或 login"),
        };

        var result = await emailCodes.SendAsync(request.Email, purpose, GetClientIp(context), cancellationToken);
        return Results.Json(new SendEmailCodeResponse(result.CooldownSec, result.DevCode));
    }

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
}
