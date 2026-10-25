using Chat.Server.Common.Options;
using Chat.Server.Data;
using Chat.Server.Modules.Files;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Chat.Server.BackgroundJobs;

/// <summary>
/// 孤儿文件清理（M3-02）：定期删除 files 中「无任何 messages.file_id 引用」且创建时间早于 24 小时的
/// 记录与磁盘文件（见 protocol/schema.sql 中 messages.file_id 的注释约定）。
/// 启动 30 秒后首轮，之后按 App:OrphanFileCleanupIntervalHours 轮询；单轮上限 500 条；异常只记日志不退出。
/// </summary>
public sealed class OrphanFileCleanupService : BackgroundService
{
    private const int FirstRunDelaySeconds = 30;
    private const int MaxFilesPerRound = 500;
    private const long RetentionMilliseconds = 24L * 60 * 60 * 1000;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly FileStorage _storage;
    private readonly ILogger<OrphanFileCleanupService> _logger;
    private readonly TimeSpan _interval;

    public OrphanFileCleanupService(
        IServiceScopeFactory scopeFactory,
        FileStorage storage,
        IOptions<AppOptions> options,
        ILogger<OrphanFileCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _storage = storage;
        _logger = logger;

        var hours = Math.Clamp(options.Value.OrphanFileCleanupIntervalHours, 1, 24 * 7);
        _interval = TimeSpan.FromHours(hours);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // 首轮延迟：避免与启动期迁移/预热抢资源，也给运维留出观察窗口。
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(FirstRunDelaySeconds), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CleanupOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "孤儿文件清理失败，将在下一轮重试");
            }

            try
            {
                await Task.Delay(_interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task CleanupOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - RetentionMilliseconds;

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ChatDbContext>();

        var candidates = await db.Files
            .Where(file => file.CreatedAt < cutoff)
            .Where(file => !db.Messages.Any(message => message.FileId == file.Id))
            .OrderBy(file => file.CreatedAt)
            .Take(MaxFilesPerRound)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            _logger.LogDebug("孤儿文件清理：本轮无待清理文件");
            return;
        }

        var deleted = 0;
        foreach (var file in candidates)
        {
            try
            {
                _storage.Delete(file.StoragePath);
            }
            catch (Exception exception)
            {
                // 磁盘删除失败时保留记录，下一轮再试；不影响其它文件。
                _logger.LogWarning(exception, "删除孤儿文件磁盘数据失败，跳过：{FileId}", file.Id);
                continue;
            }

            db.Files.Remove(file);
            deleted++;
            _logger.LogInformation("清理孤儿文件：{FileId}（created_at={CreatedAt}）", file.Id, file.CreatedAt);
        }

        if (deleted > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation("孤儿文件清理完成：待清理 {CandidateCount}，已删除 {DeletedCount}", candidates.Count, deleted);
    }
}
