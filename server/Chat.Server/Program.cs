using System.Reflection;
using Chat.Protocol;
using Chat.Server.Api;
using Chat.Server.BackgroundJobs;
using Chat.Server.Common.Configuration;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Auth;
using Chat.Server.Modules.Conversations;
using Chat.Server.Modules.Email;
using Chat.Server.Modules.Files;
using Chat.Server.Modules.Messages;
using Chat.Server.Modules.Realtime;
using Chat.Server.Realtime;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

// EiseChat 服务端启动（M0-03 骨架；M1-02 EF Core + SQLite 迁移；M1-03 配置/密钥/统一错误；
// M1-05 注册/登录/JWT；M1-06 WebSocket 握手/心跳/单活动连接；M1-07 单聊会话创建与列表；
// M1-08 消息收发（seq/幂等/ack/广播）、sync 补拉与历史分页；M3-02 文件上传/下载与孤儿清理）。
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

// REST JSON 约定与 WS 帧保持一致（protocol/protocol.md §2）：camelCase、枚举小写字符串、null 字段省略，
// 这样 Conversation 等协议 DTO 在 REST 与 WS 两处的序列化结果一致（前端按 frames.d.ts 解析）。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    var serializer = options.SerializerOptions;
    serializer.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    serializer.DefaultIgnoreCondition =
        System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull;
    serializer.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(
        System.Text.Json.JsonNamingPolicy.CamelCase,
        allowIntegerValues: false));
});

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

// 认证（M1-05）：JWT HS256（签发见 Modules/Auth/JwtTokenService）；密码 PBKDF2；登录失败锁定为单进程内存。
builder.Services.AddScoped<JwtTokenService>();
builder.Services.AddSingleton<LoginAttemptLimiter>();
builder.Services.AddScoped<AuthService>();

// 会话（M1-07 + M3-04）：单聊/群聊创建、列表与成员权限；变更经 WS conv.updated 推送；系统消息由 SystemMessageWriter 写入。
builder.Services.AddScoped<SystemMessageWriter>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddSingleton<ConversationRealtimeNotifier>();

// 消息（M1-08）：发送/补拉业务在 MessageService，WS 帧编排在 Realtime.MessageHub。
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<MessageHub>();

// 已读与在线状态（M3-05）：read.update 只增不减落库；presence 由连接生命周期触发推送。
builder.Services.AddScoped<ReadService>();
builder.Services.AddSingleton<PresenceService>();

// 文件（M3-02）：磁盘存储为单例（无状态，根目录固定）；上传/下载业务按请求作用域访问数据库。
builder.Services.AddSingleton<FileStorage>();
builder.Services.AddScoped<FileService>();

// 孤儿文件清理（M3-02）：启动 30 秒后首轮，之后按 App:OrphanFileCleanupIntervalHours 轮询（默认 24 小时）。
builder.Services.AddHostedService<OrphanFileCleanupService>();

// 实时通信（M1-06）：WS 握手/心跳/单活动连接；M1-08 起接入 message.send / sync.request。
builder.Services.AddSingleton<ConnectionManager>();
builder.Services.AddSingleton<WebSocketConnectionHandler>();
builder.Services.AddScoped<WsAuthenticator>();
builder.Services.AddHostedService<HeartbeatService>();

// JWT Bearer 校验：除签名/过期外，还查 sessions（jti 未撤销）与 users.status（未禁用），实现 logout 即失效。
var jwtAuthOptions = builder.Configuration.GetSection(JwtOptions.SectionName).Get<JwtOptions>() ?? new JwtOptions();
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.MapInboundClaims = false;
        // 与 WS auth 帧校验共用同一套参数（Realtime/JwtTokenValidator），避免两处规则漂移。
        options.TokenValidationParameters = JwtTokenValidator.CreateTokenValidationParameters(jwtAuthOptions);
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var db = context.HttpContext.RequestServices.GetRequiredService<ChatDbContext>();
                var principal = context.Principal;
                var userId = principal?.FindFirst("sub")?.Value;
                var jwtId = principal?.FindFirst("jti")?.Value;
                if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(jwtId))
                {
                    context.Fail("令牌缺少 sub/jti");
                    return;
                }

                var cancellationToken = context.HttpContext.RequestAborted;
                var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var session = await db.Sessions.AsNoTracking()
                    .FirstOrDefaultAsync(item => item.JwtId == jwtId, cancellationToken);
                if (session is null ||
                    session.RevokedAt is not null ||
                    !string.Equals(session.UserId, userId, StringComparison.Ordinal))
                {
                    context.Fail("会话不存在或已撤销");
                    return;
                }

                var status = await db.Users.AsNoTracking()
                    .Where(item => item.Id == userId)
                    .Select(item => (UserStatus?)item.Status)
                    .FirstOrDefaultAsync(cancellationToken);
                if (status != UserStatus.Active)
                {
                    context.Fail("账号不可用");
                    return;
                }

                // 低频刷新最近活跃时间（60 秒节流），避免每个已认证请求都写库。
                if (now - session.LastSeenAt >= 60_000)
                {
                    await db.Sessions
                        .Where(item => item.Id == session.Id)
                        .ExecuteUpdateAsync(setters => setters.SetProperty(item => item.LastSeenAt, now), cancellationToken);
                }
            },
        };
    });
builder.Services.AddAuthorization();

// 启动期需要绝对路径：先按配置读取一次（DI 中的 IOptions 需等 app 构建后才可读取）。
var appOptions = builder.Configuration.GetSection(AppOptions.SectionName).Get<AppOptions>() ?? new AppOptions();
var dataDirectory = ResolvePath(builder.Environment.ContentRootPath, appOptions.DataDirectory, "../../data");
var fileDirectory = ResolvePath(builder.Environment.ContentRootPath, appOptions.FileDirectory, "../../data/files");
Directory.CreateDirectory(dataDirectory);
Directory.CreateDirectory(fileDirectory);

// 上传请求体上限：Kestrel 默认 30MB，若不显式放大，50MB 上传会被截断（M3-02）。
// 表单整体上限同步放大到「上传上限 + 1MB」（留出 multipart 头部/边界余量）。
var requestBodyLimit = appOptions.UploadMaxBytes + 1024 * 1024;
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = requestBodyLimit);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = requestBodyLimit);

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

// 认证/授权：JWT Bearer 校验（含 sessions 撤销检查）；401/403 由上方状态码兜底输出统一错误体。
app.UseAuthentication();
app.UseAuthorization();

// WebSocket（M1-06）：GET /ws 握手；连接建立后 10 秒内必须发送 auth 帧（其它请求继续走后续中间件）。
app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
app.UseMiddleware<WsMiddleware>();

// 静态文件：前端构建产物复制到 wwwroot/（构建生成，不手改，见 docs/project-overview.md §7.4）。
app.UseDefaultFiles();
app.UseStaticFiles();

// 认证/用户 REST：M1-04 发码、M1-05 注册/登录/注销与当前用户资料。
app.MapAuthEndpoints();
app.MapUserEndpoints();

// 会话 REST（M1-07）：创建单聊、会话列表、单会话读取。
app.MapConversationsEndpoints();

// 消息 REST（M1-08）：历史分页（正常收发走 WebSocket）。
app.MapMessageEndpoints();

// 文件 REST（M3-02）：上传（multipart/form-data）与下载（会话成员鉴权、流式返回）。
app.MapFileEndpoints();

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
