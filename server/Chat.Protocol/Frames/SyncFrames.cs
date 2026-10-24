namespace Chat.Protocol.Frames;

/// <summary>sync.request 的单个会话游标（protocol/protocol.md §4.4）。</summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="LastSeq">客户端本地已收到的最大序号；0 表示从最早开始。</param>
public sealed record SyncCursor(string ConversationId, long LastSeq);

/// <summary>sync.request（C→S，补拉请求）：protocol/protocol.md §4.4。</summary>
/// <param name="Cursors">每会话 { conversationId, lastSeq }；1~100 项，会话不得重复。</param>
public sealed record SyncRequestFrame(IReadOnlyList<SyncCursor> Cursors) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.SyncRequest;
}

/// <summary>sync.response 的单个会话批次（protocol/protocol.md §4.4）。</summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="Messages">Message 数组，按 seq 升序；每条 seq &gt; lastSeq。</param>
public sealed record SyncBatch(string ConversationId, IReadOnlyList<Message> Messages);

/// <summary>sync.response（S→C，补拉响应）：protocol/protocol.md §4.4。</summary>
/// <param name="Batches">每会话一批；无新消息的会话不出现。</param>
/// <param name="HasMore">true 表示至少一个会话还有未返回消息，需继续请求。</param>
public sealed record SyncResponseFrame(IReadOnlyList<SyncBatch> Batches, bool HasMore) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.SyncResponse;
}
