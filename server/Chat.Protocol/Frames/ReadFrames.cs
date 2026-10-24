namespace Chat.Protocol.Frames;

/// <summary>read.update（C→S，上报已读游标）：protocol/protocol.md §4.6。</summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="LastReadSeq">已读到的最新序号（打开会话滚到底部时上报）。</param>
public sealed record ReadUpdateFrame(string ConversationId, long LastReadSeq) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.ReadUpdate;
}

/// <summary>read.updated（S→C，已读广播，仅单聊推送给对方）：protocol/protocol.md §4.6。</summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="UserId">已读游标所属用户。</param>
/// <param name="LastReadSeq">该用户最新的已读序号。</param>
public sealed record ReadUpdatedFrame(string ConversationId, string UserId, long LastReadSeq) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.ReadUpdated;
}
