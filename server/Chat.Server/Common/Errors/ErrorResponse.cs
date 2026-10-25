namespace Chat.Server.Common.Errors;

/// <summary>
/// 统一 REST 错误响应体，序列化后字段名小写：{ code, message, traceId }
/// （与 protocol/protocol.md §5 约定一致；客户端提示可带 traceId 便于排查）。
/// </summary>
public sealed record ErrorResponse(string Code, string Message, string TraceId);
