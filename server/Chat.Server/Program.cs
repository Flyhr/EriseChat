using System.Reflection;
using Chat.Server.Api;
using Chat.Server.Common.Configuration;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Auth;
using Chat.Server.Modules.Email;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// EiseChat 服务端启动（M0-03 骨架；M1-02 接入 EF Core + SQLite 迁移；M1-03 配置/密钥/统一错误）。
// 配置来源优先级：secrets.json（可选，gitignore）> 环境变量 > appsettings.{Environment}.json > appsettings.json；
// 端口、数据/文件目录、上传上限、注册开关、邮箱域名白名单、JWT、邮件、限流阈值均走配置。
// 业务模块（认证、消息、WS 帧处理）从 M1 后续批次逐步接入。

var builder = WebApplication.CreateBuilder(args);

// secrets.json：可选加载（不入库）；首次启动缺少 JWT 密钥时生成 32 字节随机 Base64 写入文件，日志不输出密钥值。
SecretsBootstrapper.AddJsonFile(builder.Configuration, builder.Environment.ContentRootPath);
var jwtKeyGenerated = SecretsBootstrapper.EnsureJwtKey(builder.Configuration, builder.Environment.ContentRootPath);

// 强类型配置项（Common/Options）：按配置节绑定，缺失时使用类内默认值（默认值即 AGENTS §6 阈值）。
builder.Services.Configure<AppOptions>(builder.Configuration.GetSection(AppOptions.SectionName));
builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection(JwtOptions.SectionName));
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection(EmailOptions.SectionName));
builder.Services.Configure<RateLimitOptions>(builder.Configuration.GetSection(RateLimitOptions.SectionName));

// 邮件通道：Development 用 Mock（验证码输出到控制台 stdout，不接真实邮件）；其它环境注册 SMTP 占位实现（M1-04 不真实发信）。
if (builder.Environment.IsDevelopment())
{
    builder.Services.AddScoped<IEmailSender, MockEmailSender>();
}
else
{
    builder.Services.AddScoped<IEmailSender, SmtpEmailSender>();
}

// 验证码发送（M1-04）：限流计数为单进程内存（重启清零）；EmailCodeService 负责生成/哈希/落库/发信。
builder.Services.AddSingleton<EmailCodeRateLimiter>();
builder.Services.AddScoped<EmailCodeService>();

// 启动期需要绝对路径：先按配置读取一次（DI 中的 IOptions 需等 app 构建后才可读取）。
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
var dataDirectory = ResolvePath(builder.Environment.ContentRootPath, appOptions.DataDirectory, "../../data");
var fileDirectory = ResolvePath(builder.Environment.ContentRootPath, appOptions.FileDirectory, "../../data/files");
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(fileDirectory);

var databasePath = Path.Combine(dataDirectory, "chat.db");

// 连接串由绝对路径生成，避免相对路径随工作目录漂移；Foreign Keys=True 让驱动为每条连接开启外键。
var connectionString = new SqliteConnectionStringBuilder
{
    DataSource = databasePath,
    ForeignKeys = true,
}.ToString();

// PRAGMA 由拦截器在每次打开连接时执行（WAL 为库级设置；foreign_keys/busy_timeout 为连接级设置）。
builder.Services.AddSingleton<SqlitePragmaInterceptor>();
builder.Services.AddDbContext<ChatDbContext>((services, options) =>
    options.UseSqlite(connectionString)
        .AddInterceptors(services.GetRequiredService<SqlitePragmaInterceptor>()));

var app = builder.Build();

if (jwtKeyGenerated)
{
    app.Logger.LogInformation("首次启动：已生成 JWT 密钥并写入 secrets.json（日志不输出密钥值）");
}

// 启动即迁移（幂等，EF 用 __EFMigrationsHistory 判断）；失败则终止启动，避免带着半成品库运行。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    db.Database.Migrate();
}

// 统一错误处理：异常中间件在最外层；状态码兜底为 404/405 等无响应体的响应补 JSON
// （后续鉴权中间件返回 401/403 时同样复用该错误体）。
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseStatusCodePages(async context =>
{
    var httpContext = context.HttpContext;
    if (httpContext.Response.HasStarted || httpContext.Response.ContentLength is not null)
    {
        return;
    }

    var (code, message) = RestErrorWriter.MapStatusCode(httpContext.Response.StatusCode);
    await RestErrorWriter.WriteAsync(httpContext, httpContext.Response.StatusCode, code, message);
});

// 静态文件：前端构建产物复制到 wwwroot/（构建生成，不手改，见 docs/project-overview.md §7.4）。
app.UseDefaultFiles();
app.UseStaticFiles();

// 认证 REST：M1-04 起逐步接入（当前仅发送邮箱验证码）。
app.MapAuthEndpoints();

// 健康检查：部署与发版检查使用（docs/project-overview.md §13.3）；db 探活执行 SELECT 1。
app.MapGet("/api/health", async (ChatDbContext db, IOptions<AppOptions> options) =>
{
    var protocolVersion = options.Value.ProtocolVersion;
    var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    var database = "error";
    try
    {
        await db.Database.OpenConnectionAsync();
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT 1";
        var value = await command.ExecuteScalarAsync();
        if (value is not null && Convert.ToInt64(value) == 1L)
        {
            database = "ok";
        }
    }
    catch (Exception exception)
    {
        app.Logger.LogWarning(exception, "健康检查数据库探活失败");
    }
    finally
    {
        try
        {
            await db.Database.CloseConnectionAsync();
        }
        catch (Exception exception)
        {
            app.Logger.LogDebug(exception, "健康检查关闭数据库连接失败");
        }
    }

    return Results.Json(new
    {
        status = database == "ok" ? "ok" : "degraded",
        serverTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        version,
        protocolVersion,
        db = database,
    });
});

app.Run();

// 把配置中的相对路径解析为绝对路径（相对内容根目录；默认值按仓库根 data/ 约定）。
static string ResolvePath(string contentRootPath, string? configured, string fallbackRelative)
{
    var relative = string.IsNullOrWhiteSpace(configured) ? fallbackRelative : configured;
    return Path.GetFullPath(Path.Combine(contentRootPath, relative));
}
