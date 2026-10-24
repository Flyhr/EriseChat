namespace Chat.Protocol.Frames;

/// <summary>typing（C→S，正在输入）：protocol/protocol.md §4.7。userId 以连接身份为准，客户端无需发送。</summary>
/// <param name="ConversationId">目标会话 ID。</param>
public sealed record TypingClientFrame(string ConversationId) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Typing;
}

/// <summary>typing（S→C，正在输入转发）：protocol/protocol.md §4.7。</summary>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="UserId">正在输入的用户 ID。</param>
public sealed record TypingServerFrame(string ConversationId, string UserId) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Typing;
}

/// <summary>presence（S→C，在线状态）：protocol/protocol.md §4.7。</summary>
/// <param name="UserId">用户 ID。</param>
/// <param name="Online">是否在线。</param>
public sealed record PresenceFrame(string UserId, bool Online) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Presence;
}

/// <summary>kicked（S→C，被踢下线）：protocol/protocol.md §4.8。发送后服务端关闭旧连接（4001）。</summary>
/// <param name="Reason">原因描述，如「账号在其他设备登录」。</param>
public sealed record KickedFrame(string Reason) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Kicked;
}
