using System.Security.Claims;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Security;
using Chat.Server.Modules.Files;
using Microsoft.AspNetCore.Http;

namespace Chat.Server.Api;

/// <summary>
/// 文件 REST 边界（M3-02）：上传 <c>POST /api/files</c>（multipart/form-data，字段名 file），
/// 下载 <c>GET /api/files/{fileId}</c>（会话成员鉴权，流式返回，不暴露磁盘路径）。
/// </summary>
public static class FileEndpoints
{
    public static IEndpointRouteBuilder MapFileEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/files", UploadAsync)
            .WithName("UploadFile")
            .RequireAuthorization()
            .Produces<UploadFileResponse>();

        app.MapGet("/api/files/{fileId}", DownloadAsync)
            .WithName("DownloadFile")
            .RequireAuthorization();

        return app;
    }

    private static async Task<IResult> UploadAsync(
        HttpContext context,
        FileService files,
        CancellationToken cancellationToken)
    {
        if (!context.Request.HasFormContentType)
        {
            throw ApiException.ValidationFailed("请求必须为 multipart/form-data");
        }

        // 手动读取表单：避免 minimal API IFormFile 绑定在 .NET 8+ 触发表单反伪造中间件要求（M3-02）。
        var form = await context.Request.ReadFormAsync(cancellationToken);
        var file = form.Files["file"];
        if (file is null)
        {
            throw ApiException.ValidationFailed("缺少文件字段 file");
        }

        if (form.Files.Count != 1)
        {
            throw ApiException.ValidationFailed("一次只能上传 1 个文件");
        }

        var userId = context.User.UserId();
        await using var stream = file.OpenReadStream();
        var result = await files.UploadAsync(userId, file.FileName, stream, file.Length, cancellationToken);
        return Results.Json(new UploadFileResponse(result.FileId, result.Name, result.Size, result.Mime, result.CreatedAt));
    }

    private static async Task<IResult> DownloadAsync(
        string fileId,
        HttpContext context,
        FileService files,
        CancellationToken cancellationToken)
    {
        var userId = context.User.UserId();
        var result = await files.DownloadAsync(userId, fileId, cancellationToken);

        var isImage = result.File.Mime.StartsWith("image/", StringComparison.Ordinal);
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        if (isImage)
        {
            // 图片用 img 标签展示，允许浏览器私有缓存；其它类型按附件下载。
            context.Response.Headers.CacheControl = "private, max-age=86400";
        }

        return Results.Stream(
            result.Content,
            contentType: result.File.Mime,
            fileDownloadName: isImage ? null : result.File.OriginalName,
            lastModified: DateTimeOffset.FromUnixTimeMilliseconds(result.File.CreatedAt),
            enableRangeProcessing: true);
    }

    /// <summary>上传响应（字段与 apps/web/src/api/files.ts 的 UploadFileResponse 一致）。</summary>
    public sealed record UploadFileResponse(string FileId, string Name, long Size, string Mime, long CreatedAt);
}
