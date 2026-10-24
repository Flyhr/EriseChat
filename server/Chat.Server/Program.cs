using Chat.Protocol;

// EiseChat 服务端最小启动骨架（M0-03）。
// 端口/数据目录/协议版本均由配置提供，可用 appsettings*.json、环境变量
// （如 Kestrel__Endpoints__Http__Url、App__ProtocolVersion）或命令行参数覆盖。
// 业务模块（认证、消息、WS 帧处理、EF 迁移）从 M1 起逐步接入，本文件只负责启动、
// 静态文件与健康检查，保持可构建、可启动。

var builder = WebApplication.CreateBuilder(args);

var app = builder.Build();

var protocolVersion = app.Configuration.GetValue("App:ProtocolVersion", ProtocolVersion.Current);

// 静态文件：前端构建产物复制到 wwwroot/（构建生成，不手改，见 docs/project-overview.md §7.4）；
// 目录不存在时静态文件中间件自动跳过，不影响启动。
app.UseDefaultFiles();
app.UseStaticFiles();

// 健康检查：部署与发版检查使用（docs/project-overview.md §13.3），返回状态、服务器时间与协议版本。
app.MapGet("/api/health", () => Results.Json(new
{
    status = "ok",
    serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
    protocolVersion,
}));

app.Run();
