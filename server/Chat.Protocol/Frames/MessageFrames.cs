namespace Chat.Protocol.Frames;

/// <summary>message.send（C→S，发送消息）：protocol/protocol.md §4.3。</summary>
/// <param name="ClientMsgId">客户端生成的 UUID；会话内唯一，幂等键。</param>
/// <param name="ConversationId">目标会话 ID。</param>
/// <param name="MsgType">text / image / file；system 仅服务端生成，客户端发送会被拒绝。</param>
/// <param name="Content">text 为字符串；image / file 为 <see cref="FileContent"/>。</param>
/// <param name="ReplyTo">被回复消息的 messageId。</param>
public sealed record MessageSendFrame(
    string ClientMsgId,
    string ConversationId,
    MessageType MsgType,
    MessageContent Content,
    string? ReplyTo = null) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.MessageSend;
}

/// <summary>message.ack（S→C，落库回执，仅发送者连接收到）：protocol/protocol.md §4.3。</summary>
/// <param name="ClientMsgId">回传客户端幂等键。</param>
/// <param name="MessageId">服务端消息 ID（messages.id）。</param>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="Seq">会话内序号，严格 +1。</param>
/// <param name="CreatedAt">落库时间（UTC 毫秒）。</param>
/// <param name="Reply">回复引用摘要（可选，F-09）；仅当本次为回复且被引用消息存在时返回。</param>
public sealed record MessageAckFrame(
    string ClientMsgId,
    string MessageId,
    string ConversationId,
    long Seq,
    long CreatedAt,
    MessageReply? Reply = null) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.MessageAck;
}

/// <summary>message.new（S→C，新消息广播，发送连接除外）：protocol/protocol.md §4.3。</summary>
/// <param name="Message">消息对象。</param>
public sealed record MessageNewFrame(Message Message) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.MessageNew;
}
