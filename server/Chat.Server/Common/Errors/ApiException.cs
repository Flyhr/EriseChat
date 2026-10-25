using Chat.Protocol;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Common.Errors;

/// <summary>
/// 业务异常：模块/端点抛出后由 <see cref="ExceptionHandlingMiddleware"/> 统一转成
/// REST 错误响应 { code, message, traceId }，code 取自 Chat.Protocol.ErrorCodes。
/// </summary>
public sealed class ApiException : Exception
{
    public ApiException(string code, string message, int statusCode = StatusCodes.Status400BadRequest, Exception? innerException = null)
        : base(message, innerException)
    {
        Code = code;
        StatusCode = statusCode;
    }

    /// <summary>错误码（Chat.Protocol.ErrorCodes 中的常量）。</summary>
    public string Code { get; }

    /// <summary>HTTP 状态码。</summary>
    public int StatusCode { get; }

    public static ApiException ValidationFailed(string message) =>
        new(ErrorCodes.ValidationFailed, message);

    public static ApiException Unauthorized(string message) =>
        new(ErrorCodes.Unauthorized, message, StatusCodes.Status401Unauthorized);

    public static ApiException Forbidden(string message) =>
        new(ErrorCodes.Forbidden, message, StatusCodes.Status403Forbidden);

    public static ApiException NotFound(string message) =>
        new(ErrorCodes.NotFound, message, StatusCodes.Status404NotFound);

    public static ApiException RateLimited(string message) =>
        new(ErrorCodes.RateLimited, message, StatusCodes.Status429TooManyRequests);
}
