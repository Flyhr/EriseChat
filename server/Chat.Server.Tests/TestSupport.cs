using System.Security.Cryptography;
using Chat.Server.Common.Errors;
using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Data.Entities;
using Chat.Server.Modules.Auth;
using Chat.Server.Modules.Conversations;
using Chat.Server.Modules.Email;
using Chat.Server.Modules.Messages;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using ConversationMemberEntity = Chat.Server.Data.Entities.ConversationMember;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Tests;

/// <summary>
/// M1-09 单测基座：每个测试类/用例使用独立临时文件 SQLite 库（执行真实 InitialCreate 迁移），
/// 不污染仓库 data/；并发用例通过同一库多连接验证 seq 唯一性。
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly string _directory;

    private TestDatabase(string directory, string dbPath, DbContextOptions<ChatDbContext> options)
    {
        _directory = directory;
        DbPath = dbPath;
        Options = options;
    }

    public string DbPath { get; }

    public DbContextOptions<ChatDbContext> Options { get; }

    public static async Task<TestDatabase> CreateAsync()
    {
        var directory = Path.Combine(Path.GetTempPath(), "eisechat-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var dbPath = Path.Combine(directory, "chat.db");

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            DefaultTimeout = 30,
            Pooling = false,
        }.ToString();

        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite(connectionString)
            .AddInterceptors(new SqlitePragmaInterceptor())
            .Options;

        await using (var db = new ChatDbContext(options))
        {
            await db.Database.MigrateAsync();
        }

        return new TestDatabase(directory, dbPath, options);
    }

    public ChatDbContext CreateContext() => new(Options);

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // 进程内仍有句柄时忽略：临时目录由系统清理，不影响测试结论。
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>测试用环境（Development 才会启用 Mock 邮件固定码）。</summary>
public sealed class TestHostEnvironment : IHostEnvironment
{
    public string EnvironmentName { get; set; } = Environments.Development;

    public string ApplicationName { get; set; } = "Chat.Server.Tests";

    public string ContentRootPath { get; set; } = Path.GetTempPath();

    public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
        new Microsoft.Extensions.FileProviders.NullFileProvider();
}

/// <summary>测试用服务工厂与数据种子（真实业务类，不 mock 业务逻辑）。</summary>
public static class TestSupport
{
    public static AppOptions CreateAppOptions() => new()
    {
        DataDirectory = ".", FileDirectory = ".", UploadMaxBytes = 50L * 1024 * 1024, ProtocolVersion = 1,
    };

    public static JwtOptions CreateJwtOptions() => new()
    {
        Issuer = "eisechat",
        Audience = "eisechat",
        ExpireDays = 7,
        ClockSkewSeconds = 30,
        Key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
    };

    public static RateLimitOptions CreateRateLimitOptions() => new()
    {
        EmailCodePerEmailIntervalSeconds = 60,
        EmailCodePerEmailDaily = 10,
        EmailCodePerIpHourly = 20,
        EmailCodeMaxAttempts = 5,
        LoginFailedMaxAttempts = 5,
        LoginLockMinutes = 5,
    };

    public static EmailOptions CreateEmailOptions(string fixedCode = "123456") => new()
    {
        Mock = new MockEmailOptions { FixedCode = fixedCode },
    };

    public static MessageService CreateMessageService(ChatDbContext db) =>
        new(db, Options.Create(CreateAppOptions()));

    /// <summary>M3-06：系统消息写入器（群成员变更系统消息）。</summary>
    public static SystemMessageWriter CreateSystemMessageWriter(ChatDbContext db) => new(db);

    /// <summary>M3-06：会话业务（真实 SystemMessageWriter + AppOptions，可按用例覆盖群配置）。</summary>
    public static ConversationService CreateConversationService(ChatDbContext db, AppOptions? appOptions = null) =>
        new(
            db,
            new SystemMessageWriter(db),
            Options.Create(appOptions ?? CreateAppOptions()),
            NullLogger<ConversationService>.Instance);

    public static AuthService CreateAuthService(
        ChatDbContext db,
        JwtOptions jwtOptions,
        RateLimitOptions? rateLimitOptions = null)
    {
        var jwt = Options.Create(jwtOptions);
        var rate = Options.Create(rateLimitOptions ?? CreateRateLimitOptions());
        return new AuthService(
            db,
            jwt,
            rate,
            new JwtTokenService(jwt),
            new LoginAttemptLimiter(rate),
            NullLogger<AuthService>.Instance);
    }

    public static EmailCodeService CreateEmailCodeService(
        ChatDbContext db,
        JwtOptions jwtOptions,
        EmailOptions emailOptions,
        RateLimitOptions rateLimitOptions) =>
        new(
            db,
            Options.Create(CreateAppOptions()),
            Options.Create(jwtOptions),
            Options.Create(emailOptions),
            Options.Create(rateLimitOptions),
            new EmailCodeRateLimiter(Options.Create(rateLimitOptions)),
            new MockEmailSender(NullLogger<MockEmailSender>.Instance),
            new TestHostEnvironment(),
            NullLogger<EmailCodeService>.Instance);

    public static async Task<User> SeedUserAsync(
        ChatDbContext db,
        string email,
        string displayName = "测试用户",
        string? passwordHash = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var user = new User
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = email,
            DisplayName = displayName,
            PasswordHash = passwordHash,
            EmailVerifiedAt = now,
            Role = Chat.Protocol.UserRole.User,
            Status = Chat.Protocol.UserStatus.Active,
            CreatedAt = now,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    public static async Task<ConversationEntity> SeedDirectConversationAsync(
        ChatDbContext db,
        User owner,
        User member)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var directKey = string.CompareOrdinal(owner.Id, member.Id) <= 0
            ? $"{owner.Id}:{member.Id}"
            : $"{member.Id}:{owner.Id}";
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = Chat.Protocol.ConversationType.Direct,
            Title = null,
            DirectKey = directKey,
            CreatedBy = owner.Id,
            CreatedAt = now,
            LastSeq = 0,
        };
        db.Conversations.Add(conversation);
        db.ConversationMembers.Add(new ConversationMemberEntity
        {
            ConversationId = conversation.Id,
            UserId = owner.Id,
            Role = Chat.Protocol.MemberRole.Owner,
            JoinedAt = now,
            LastReadSeq = 0,
        });
        db.ConversationMembers.Add(new ConversationMemberEntity
        {
            ConversationId = conversation.Id,
            UserId = member.Id,
            Role = Chat.Protocol.MemberRole.Member,
            JoinedAt = now,
            LastReadSeq = 0,
        });
        await db.SaveChangesAsync();
        return conversation;
    }

    /// <summary>M3-06：种子群聊（owner + members，lastSeq=0，无系统消息）。</summary>
    public static async Task<ConversationEntity> SeedGroupConversationAsync(
        ChatDbContext db,
        User owner,
        params User[] members)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var conversation = new ConversationEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            Type = Chat.Protocol.ConversationType.Group,
            Title = "测试群聊",
            DirectKey = null,
            CreatedBy = owner.Id,
            CreatedAt = now,
            LastSeq = 0,
        };
        db.Conversations.Add(conversation);
        db.ConversationMembers.Add(new ConversationMemberEntity
        {
            ConversationId = conversation.Id,
            UserId = owner.Id,
            Role = Chat.Protocol.MemberRole.Owner,
            JoinedAt = now,
            LastReadSeq = 0,
        });
        foreach (var member in members)
        {
            db.ConversationMembers.Add(new ConversationMemberEntity
            {
                ConversationId = conversation.Id,
                UserId = member.Id,
                Role = Chat.Protocol.MemberRole.Member,
                JoinedAt = now,
                LastReadSeq = 0,
            });
        }

        await db.SaveChangesAsync();
        return conversation;
    }

    /// <summary>批量种子消息（直接落库，避免 250 次发送拖慢测试）。</summary>
    public static async Task SeedMessagesAsync(
        ChatDbContext db,
        string conversationId,
        string senderId,
        int count,
        int startSeq = 1)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        string? lastMessageId = null;
        for (var index = 0; index < count; index++)
        {
            var seq = startSeq + index;
            var entity = new MessageEntity
            {
                Id = Guid.NewGuid().ToString("D"),
                ConversationId = conversationId,
                SenderId = senderId,
                ClientMsgId = Guid.NewGuid().ToString("N"),
                Seq = seq,
                MsgType = Chat.Protocol.MessageType.Text,
                ContentJson = $"\"m{seq}\"",
                CreatedAt = now + seq,
            };
            db.Messages.Add(entity);
            lastMessageId = entity.Id;
        }

        var conversation = await db.Conversations.FirstAsync(item => item.Id == conversationId);
        conversation.LastSeq = startSeq + count - 1;
        conversation.LastMessageId = lastMessageId;
        await db.SaveChangesAsync();
    }

    /// <summary>断言 ApiException 的错误码（不依赖异常消息文案）。</summary>
    public static async Task AssertApiExceptionAsync(
        string expectedCode,
        Func<Task> action,
        int? expectedStatus = null)
    {
        var exception = await Assert.ThrowsAsync<ApiException>(action);
        Assert.Equal(expectedCode, exception.Code);
        if (expectedStatus is not null)
        {
            Assert.Equal(expectedStatus.Value, exception.StatusCode);
        }
    }
}
