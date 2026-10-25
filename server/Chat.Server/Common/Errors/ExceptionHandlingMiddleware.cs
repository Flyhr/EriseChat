using Chat.Protocol;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Common.Errors;

/// <summary>
/// 统一异常处理中间件（管道最外层）：
///   * <see cref="ApiException"/>：业务异常，按自带 HTTP 状态码返回 { code, message, traceId }；
///   * <see cref="BadHttpRequestException"/>：请求体/参数无法解析，返回 400 VALIDATION_FAILED；
///   * 其他异常：500 INTERNAL_ERROR，日志按 traceId 记录；开发环境返回异常摘要、生产环境不泄露内部细节。
/// 敏感值（token、密码、验证码、邮件内容、密钥）不写入日志与响应。
/// </summary>
public sealed class ExceptionHandlingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<ExceptionHandlingMiddleware> _logger;
    private readonly IHostEnvironment _environment;

    public ExceptionHandlingMiddleware(
        RequestDelegate next,
        ILogger<ExceptionHandlingMiddleware> logger,
        IHostEnvironment environment)
    {
        _next = next;
        _logger = logger;
        _environment = environment;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // 客户端断开属于正常取消，不记录为错误。
            _logger.LogDebug("请求已被客户端取消 traceId={TraceId}", RestErrorWriter.GetTraceId(context));
        }
        catch (ApiException exception)
        {
            _logger.LogWarning(
                "业务异常 traceId={TraceId} code={Code} status={Status}",
                RestErrorWriter.GetTraceId(context),
                exception.Code,
                exception.StatusCode);

            await RestErrorWriter.WriteAsync(context, exception.StatusCode, exception.Code, exception.Message);
        }
        catch (BadHttpRequestException)
        {
            // 不记录异常消息，避免把请求体片段写入日志。
            _logger.LogWarning("请求格式错误 traceId={TraceId}", RestErrorWriter.GetTraceId(context));
            await RestErrorWriter.WriteAsync(
                context,
                StatusCodes.Status400BadRequest,
                ErrorCodes.ValidationFailed,
                "请求格式错误");
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "未处理异常 traceId={TraceId}", RestErrorWriter.GetTraceId(context));

            var message = _environment.IsDevelopment()
                ? $"服务端内部错误：{exception.Message}"
                : "服务端内部错误，请稍后重试";

            await RestErrorWriter.WriteAsync(
                context,
                StatusCodes.Status500InternalServerError,
                ErrorCodes.InternalError,
                message);
        }
    }
}
