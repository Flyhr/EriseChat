using System.Net;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Chat.Server.Api;

/// <summary>
/// 下载页与安装包分发（M4-04，计划 §5.5）：
/// - <c>GET /download</c>：静态 HTML（版本号、Windows 安装包、Android APK 占位、更新日志、SmartScreen 说明）；
/// - <c>GET /download/files/{fileName}</c>：只允许 <c>EriseChat-Setup-*.exe</c> / <c>EriseChat-*.apk</c> 白名单文件，
///   经路径穿越校验后以附件下载；文件不存在或非法名一律 404。
/// 目录来自 AppOptions.DownloadDirectory（默认仓库根 data/downloads），不暴露磁盘路径。
/// </summary>
public static class DownloadEndpoints
{
    private static readonly Regex AllowedFileNamePattern = new(
        @"^EriseChat-(?:Setup-.+\.exe|.+\.apk)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IEndpointRouteBuilder MapDownloadEndpoints(this IEndpointRouteBuilder app, string downloadDirectory)
    {
        var directory = Path.GetFullPath(downloadDirectory);
        Directory.CreateDirectory(directory);

        app.MapGet("/download", (HttpRequest request) => Results.Content(BuildPage(request, directory), "text/html; charset=utf-8"))
            .WithName("DownloadPage")
            .AllowAnonymous();

        app.MapGet("/download/files/{fileName}", (string fileName) => DownloadFile(fileName, directory))
            .WithName("DownloadPackageFile")
            .AllowAnonymous();

        return app;
    }

    private static IResult DownloadFile(string fileName, string directory)
    {
        if (!IsAllowedFileName(fileName))
        {
            return Results.NotFound();
        }

        var fullPath = Path.GetFullPath(Path.Combine(directory, fileName));
        var prefix = directory.EndsWith(Path.DirectorySeparatorChar) ? directory : directory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
        {
            return Results.NotFound();
        }

        var contentType = fullPath.EndsWith(".apk", StringComparison.OrdinalIgnoreCase)
            ? "application/vnd.android.package-archive"
            : "application/vnd.microsoft.portable-executable";
        return Results.File(fullPath, contentType, fileName, enableRangeProcessing: true);
    }

    /// <summary>白名单 + 单段文件名校验（拒绝 ..、/、\、盘符等路径成分）。</summary>
    private static bool IsAllowedFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > 128)
        {
            return false;
        }

        if (!string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal) ||
            fileName.Contains("..", StringComparison.Ordinal) ||
            fileName.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
        {
            return false;
        }

        return AllowedFileNamePattern.IsMatch(fileName);
    }

    private static string BuildPage(HttpRequest request, string directory)
    {
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1.0.0";
        var installer = FindNewest(directory, "EriseChat-Setup-*.exe");
        var apk = FindNewest(directory, "EriseChat-*.apk");
        var host = WebUtility.HtmlEncode(request.Host.Value ?? string.Empty);

        var hasInstaller = installer is not null;
        var windowsLabel = hasInstaller
            ? $"下载 Windows 安装包（{FormatSize(installer!.Length)}）"
            : "安装包即将提供";
        var windowsHref = hasInstaller ? $"/download/files/{WebUtility.UrlEncode(Path.GetFileName(installer!.FullName))}" : "#";
        var windowsDescription = hasInstaller
            ? "适用于 Windows 10/11（x64），当前用户安装，无需管理员权限。"
            : "安装包尚未上传到服务器 data/downloads 目录，请管理员放置后刷新本页。";

        var hasApk = apk is not null;
        var androidLabel = hasApk ? $"下载 Android APK（{FormatSize(apk!.Length)}）" : "即将提供（M5）";
        var androidHref = hasApk ? $"/download/files/{WebUtility.UrlEncode(Path.GetFileName(apk!.FullName))}" : "#";
        var androidDescription = hasApk
            ? "适用于 Android 8.0（API 26）及以上，侧载安装（需允许未知来源）。"
            : "Android 安装包随 M5 里程碑提供，当前版本暂未提供。";

        return PageTemplate
            .Replace("{{VERSION}}", WebUtility.HtmlEncode(version), StringComparison.Ordinal)
            .Replace("{{HOST}}", host, StringComparison.Ordinal)
            .Replace("{{WINDOWS_DESC}}", WebUtility.HtmlEncode(windowsDescription), StringComparison.Ordinal)
            .Replace("{{WINDOWS_HREF}}", windowsHref, StringComparison.Ordinal)
            .Replace("{{WINDOWS_LABEL}}", WebUtility.HtmlEncode(windowsLabel), StringComparison.Ordinal)
            .Replace("{{WINDOWS_DISABLED}}", hasInstaller ? "false" : "true", StringComparison.Ordinal)
            .Replace("{{ANDROID_DESC}}", WebUtility.HtmlEncode(androidDescription), StringComparison.Ordinal)
            .Replace("{{ANDROID_HREF}}", androidHref, StringComparison.Ordinal)
            .Replace("{{ANDROID_LABEL}}", WebUtility.HtmlEncode(androidLabel), StringComparison.Ordinal)
            .Replace("{{ANDROID_DISABLED}}", hasApk ? "false" : "true", StringComparison.Ordinal);
    }

    private static FileInfo? FindNewest(string directory, string searchPattern) =>
        new DirectoryInfo(directory)
            .EnumerateFiles(searchPattern, SearchOption.TopDirectoryOnly)
            .Where(file => AllowedFileNamePattern.IsMatch(file.Name))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .FirstOrDefault();

    private static string FormatSize(long bytes) =>
        bytes >= 1024L * 1024L
            ? $"{bytes / (1024d * 1024d):0.0} MB"
            : $"{Math.Max(1, bytes / 1024d):0.0} KB";

    private const string PageTemplate = """
<!DOCTYPE html>
<html lang="zh-CN">
<head>
  <meta charset="utf-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1" />
  <title>EriseChat 下载 · v{{VERSION}}</title>
  <style>
    :root { color-scheme: light; }
    body { margin: 0; font-family: system-ui, -apple-system, "Segoe UI", sans-serif; background: #f5f6f3; color: #1f2a24; }
    main { max-width: 720px; margin: 0 auto; padding: 48px 20px 64px; }
    h1 { font-size: 28px; margin: 8px 0; }
    h2 { font-size: 18px; margin: 0 0 10px; }
    .muted { color: #5b6b62; }
    .card { background: #fff; border: 1px solid #dde4dc; border-radius: 12px; padding: 20px; margin-top: 20px; box-shadow: 0 2px 8px rgba(20, 40, 30, .05); }
    .tag { display: inline-block; padding: 2px 8px; border-radius: 999px; background: #e4efe7; color: #2f7d5d; font-size: 12px; font-weight: 600; }
    .button { display: inline-block; margin-top: 8px; padding: 10px 18px; border-radius: 8px; background: #2f7d5d; color: #fff; text-decoration: none; font-weight: 600; }
    .button[aria-disabled="true"] { background: #a8b6ad; pointer-events: none; }
    ul { margin: 8px 0 0; padding-left: 20px; line-height: 1.7; }
    code { background: #eef2ee; border-radius: 4px; padding: 1px 5px; }
    footer { margin-top: 32px; font-size: 13px; color: #5b6b62; }
  </style>
</head>
<body>
<main>
  <span class="tag">内部使用</span>
  <h1>EriseChat 下载</h1>
  <p class="muted">当前版本 <strong>{{VERSION}}</strong> · 服务器 <code>{{HOST}}</code></p>

  <section class="card">
    <h2>Windows 安装包</h2>
    <p class="muted">{{WINDOWS_DESC}}</p>
    <a class="button" href="{{WINDOWS_HREF}}" aria-disabled="{{WINDOWS_DISABLED}}">{{WINDOWS_LABEL}}</a>
    <p class="muted" style="margin-top:12px;font-size:13px;">安装包未购买代码签名证书：若 SmartScreen 提示「已保护你的电脑」，请点「更多信息」→「仍要运行」。安装到当前用户目录，无需管理员权限。</p>
  </section>

  <section class="card">
    <h2>Android APK</h2>
    <p class="muted">{{ANDROID_DESC}}</p>
    <a class="button" href="{{ANDROID_HREF}}" aria-disabled="{{ANDROID_DISABLED}}">{{ANDROID_LABEL}}</a>
  </section>

  <section class="card">
    <h2>更新日志</h2>
    <ul>
      <li>v{{VERSION}}：首个正式版（M4 收口）：Windows 安装包；群聊/图片文件/已读/typing/在线状态等 M1~M3 能力。</li>
      <li>后续版本：Android APK（M5）与自动更新（P2，未排期）。</li>
    </ul>
  </section>

  <footer>
    内部使用，请勿外传安装包；遇到问题联系管理员。服务端地址：<code>{{HOST}}</code>。
  </footer>
</main>
</body>
</html>
""";
}
