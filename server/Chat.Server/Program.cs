using System.Reflection;
using Chat.Protocol;
using Chat.Server.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

// EiseChat 服务端启动（M0-03 骨架；M1-02 起接入 EF Core + SQLite 迁移）。
// 端口/数据目录/协议版本均由配置提供（appsettings*.json、环境变量、命令行参数可覆盖）；
// 首次运行会在 data/ 目录创建 chat.db 并执行迁移，随后启动 Kestrel 与 /api/health。
// 业务模块（认证、消息、WS 帧处理）从 M1 后续批次逐步接入。

var builder = WebApplication.CreateBuilder(args);

// 数据目录：默认仓库根 data/（配置为相对内容根目录的路径，此处解析为绝对路径）。
var dataDirectory = ResolveDataDirectory(builder.Configuration, builder.Environment.ContentRootPath);
Directory.CreateDirectory(dataDirectory);
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

// 启动即迁移（幂等，EF 用 __EFMigrationsHistory 判断）；失败则终止启动，避免带着半成品库运行。
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();
    db.Database.Migrate();
}

// 静态文件：前端构建产物复制到 wwwroot/（构建生成，不手改，见 docs/project-overview.md §7.4）。
app.UseDefaultFiles();
app.UseStaticFiles();

// 健康检查：部署与发版检查使用（docs/project-overview.md §13.3）；db 探活执行 SELECT 1。
app.MapGet("/api/health", async (ChatDbContext db, IConfiguration configuration) =>
{
    var protocolVersion = configuration.GetValue("App:ProtocolVersion", ProtocolVersion.Current);
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

// 把配置中的数据目录解析为绝对路径（相对路径以内容根目录为基准；默认仓库根 data/）。
static string ResolveDataDirectory(IConfiguration configuration, string contentRootPath)
{
    var configured = configuration["App:DataDirectory"];
    if (string.IsNullOrWhiteSpace(configured))
    {
        configured = "../../data";
    }

    return Path.GetFullPath(Path.Combine(contentRootPath, configured));
}
