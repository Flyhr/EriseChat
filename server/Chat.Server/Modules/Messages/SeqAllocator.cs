using System.Globalization;
using Chat.Protocol;
using Chat.Server.Common.Errors;
using Chat.Server.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Chat.Server.Modules.Messages;

/// <summary>
/// 会话内 seq 分配（M3-04 抽取自 MessageService，供消息与系统消息共用）：
/// 在调用方事务内执行 <c>UPDATE conversations SET last_seq = last_seq + 1 ... RETURNING last_seq</c>，
/// 保证严格 +1；调用方负责事务提交/回滚。
/// </summary>
public static class SeqAllocator
{
    /// <summary>在同一事务内为会话分配下一个 seq（并已把 conversations.last_seq 原子加一）。</summary>
    public static async Task<long> AllocateAsync(
        ChatDbContext db,
        string conversationId,
        CancellationToken cancellationToken)
    {
        var dbConnection = db.Database.GetDbConnection();
        await using var command = dbConnection.CreateCommand();
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        command.CommandText = "UPDATE conversations SET last_seq = last_seq + 1 WHERE id = @id RETURNING last_seq;";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@id";
        parameter.Value = conversationId;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            throw new ApiException(ErrorCodes.InternalError, "会话序号分配失败", StatusCodes.Status500InternalServerError);
        }

        return Convert.ToInt64(result, CultureInfo.InvariantCulture);
    }
}
