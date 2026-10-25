using System.Diagnostics;
using System.Text.Json;
using Chat.Protocol;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Common.Errors;

/// <summary>统一 REST 错误响应的写出工具：异常中间件与状态码兜底共用，保证响应体字段一致。</summary>
public static class RestErrorWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>错误追踪 ID：优先用当前 Activity，缺失时退回框架的 TraceIdentifier（日志与响应共用）。</summary>
    public static string GetTraceId(HttpContext context) =>
        Activity.Current?.Id ?? context.TraceIdentifier;

    /// <summary>把无响应体的 4xx/5xx 状态码映射为错误码与可展示的中文说明。</summary>
    public static (string Code, string Message) MapStatusCode(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => (ErrorCodes.ValidationFailed, "请求参数不合法"),
        StatusCodes.Status401Unauthorized => (ErrorCodes.Unauthorized, "未登录或登录已过期"),
        StatusCodes.Status403Forbidden => (ErrorCodes.Forbidden, "没有权限执行该操作"),
        StatusCodes.Status404NotFound => (ErrorCodes.NotFound, "请求的资源不存在"),
        StatusCodes.Status405MethodNotAllowed => (ErrorCodes.ValidationFailed, "请求方法不被允许"),
        StatusCodes.Status413PayloadTooLarge => (ErrorCodes.FileTooLarge, "请求体超过大小限制"),
        StatusCodes.Status429TooManyRequests => (ErrorCodes.RateLimited, "操作过于频繁，请稍后再试"),
        >= StatusCodes.Status500InternalServerError => (ErrorCodes.InternalError, "服务端内部错误，请稍后重试"),
        _ => (ErrorCodes.ValidationFailed, "请求无法处理"),
    };

    /// <summary>写出 JSON 错误响应；响应已开始写出时直接跳过（避免破坏已发送的内容）。</summary>
    public static async Task WriteAsync(HttpContext context, int statusCode, string code, string message)
    {
        if (context.Response.HasStarted)
        {
            return;
        }

        context.Response.Clear();
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";

        var payload = new ErrorResponse(code, message, GetTraceId(context));
        await context.Response.WriteAsJsonAsync(payload, JsonOptions);
    }
}
