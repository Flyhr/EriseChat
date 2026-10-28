using System.Security.Claims;
using Chat.Server.Common.Errors;

namespace Chat.Server.Common.Security;

/// <summary>
/// 从已认证的 <see cref="ClaimsPrincipal"/> 读取身份声明（REST 端点统一入口）。
/// 取不到时统一按「登录状态已失效」处理，与 JwtBearer 认证失败语义一致；
/// 不再要求每个端点文件各自重复 FindFirst。
/// </summary>
public static class ClaimsPrincipalExtensions
{
    /// <summary>当前登录用户 ID（JWT sub 声明，兼容 NameIdentifier 回退）。</summary>
    public static string UserId(this ClaimsPrincipal principal) =>
        principal.FindFirst("sub")?.Value ??
        principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ??
        throw ApiException.Unauthorized("登录状态已失效");

    /// <summary>当前 JWT 的 jti 声明（用于会话撤销）；缺失时抛 401。</summary>
    public static string JwtId(this ClaimsPrincipal principal) =>
        principal.FindFirst("jti")?.Value ?? throw ApiException.Unauthorized("登录状态已失效");

    /// <summary>当前 JWT 的 jti 声明；缺失时返回 null（仅用于列表标注当前会话）。</summary>
    public static string? JwtIdOrNull(this ClaimsPrincipal principal) =>
        principal.FindFirst("jti")?.Value;
}
