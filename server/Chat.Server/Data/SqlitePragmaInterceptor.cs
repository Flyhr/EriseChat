using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Chat.Server.Data;

/// <summary>
/// SQLite 连接初始化拦截器（约定见 protocol/schema.sql 文件头）：
/// 每次打开连接时执行 PRAGMA journal_mode=WAL（库级持久设置）、
/// PRAGMA foreign_keys=ON、PRAGMA busy_timeout=5000（两者为连接级设置）。
/// 迁移与业务查询共用同一拦截器，避免连接池复用导致 PRAGMA 丢失。
/// </summary>
public sealed class SqlitePragmaInterceptor : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        ApplyPragmas(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        ApplyPragmas(connection);
        return base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private static void ApplyPragmas(DbConnection connection)
    {
        using var command = connection.CreateCommand();

        command.CommandText = "PRAGMA journal_mode=WAL;";
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA foreign_keys=ON;";
        command.ExecuteNonQuery();

        command.CommandText = "PRAGMA busy_timeout=5000;";
        command.ExecuteNonQuery();
    }
}
