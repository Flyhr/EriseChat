namespace Chat.Protocol.Frames;

/// <summary>conv.list（C→S，请求会话列表）：protocol/protocol.md §4.5。无额外字段。</summary>
public sealed record ConvListRequestFrame : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.ConversationList;
}

/// <summary>conv.list（S→C，会话列表响应）：protocol/protocol.md §4.5。</summary>
/// <param name="Conversations">当前用户全部会话，按 lastSeq 变化/创建时间倒序。</param>
public sealed record ConvListResponseFrame(IReadOnlyList<Conversation> Conversations) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.ConversationList;
}

/// <summary>conv.updated（S→C，会话变更推送）：protocol/protocol.md §4.5。</summary>
/// <param name="Conversations">发生变化的 Conversation 数组（客户端按 conversationId upsert）。</param>
public sealed record ConvUpdatedFrame(IReadOnlyList<Conversation> Conversations) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.ConversationUpdated;
}
