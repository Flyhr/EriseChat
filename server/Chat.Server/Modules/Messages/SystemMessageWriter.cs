using System.Text.Json;
using Chat.Protocol;
using Chat.Protocol.Frames;
using Chat.Server.Data;
using Chat.Server.Modules.Conversations;
using ConversationEntity = Chat.Server.Data.Entities.Conversation;
using MessageEntity = Chat.Server.Data.Entities.Message;

namespace Chat.Server.Modules.Messages;

/// <summary>
/// 系统消息写入（M3-04）：分配 seq → 插入 <c>msgType=system</c> 消息 → 更新会话 last_message_id。
/// 不自行开启事务（由调用方事务包裹），也不调用 SaveChanges（由调用方统一提交），保证
/// 成员变更与系统消息在同一事务内落库。
/// </summary>
public sealed class SystemMessageWriter
{
    private readonly ChatDbContext _db;

    public SystemMessageWriter(ChatDbContext db)
    {
        _db = db;
    }

    /// <summary>追加一条系统消息并返回协议 Message（调用方随后 SaveChanges 并提交事务）。</summary>
    public async Task<Message> AppendAsync(
        ConversationEntity conversation,
        string senderId,
        SystemContent content,
        CancellationToken cancellationToken)
    {
        var seq = await SeqAllocator.AllocateAsync(_db, conversation.Id, cancellationToken);
        var entity = new MessageEntity
        {
            Id = Guid.NewGuid().ToString("D"),
            ConversationId = conversation.Id,
            SenderId = senderId,
            ClientMsgId = "system-" + Guid.NewGuid().ToString("N"),
            Seq = seq,
            MsgType = MessageType.System,
            ContentJson = JsonSerializer.Serialize(content, FrameCodec.JsonOptions),
            FileId = null,
            ReplyTo = null,
            CreatedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };

        _db.Messages.Add(entity);
        conversation.LastSeq = seq;
        conversation.LastMessageId = entity.Id;
        return ConversationService.ToProtocolMessage(entity);
    }
}
