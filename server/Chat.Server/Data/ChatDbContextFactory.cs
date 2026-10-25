using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Chat.Server.Data;

/// <summary>
/// 设计时（dotnet ef）用 DbContext 工厂：只用于生成迁移，不参与运行时。
/// 运行时连接串由 Program.cs 按配置解析为绝对路径（data/chat.db）；此处的占位连接串
/// 不会真正访问数据库（迁移生成只读模型）。
/// </summary>
public sealed class ChatDbContextFactory : IDesignTimeDbContextFactory<ChatDbContext>
{
    private const string DesignTimeConnectionString = "Data Source=chat.design.db";

    public ChatDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ChatDbContext>()
            .UseSqlite(DesignTimeConnectionString)
            .Options;

        return new ChatDbContext(options);
    }
}
