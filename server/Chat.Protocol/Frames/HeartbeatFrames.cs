namespace Chat.Protocol.Frames;

/// <summary>ping（双向，心跳请求）：protocol/protocol.md §4.2。客户端每 25 秒发送一次。</summary>
/// <param name="Ts">发送方当前时间（UTC 毫秒）。</param>
public sealed record PingFrame(long Ts) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Ping;
}

/// <summary>pong（双向，心跳响应）：protocol/protocol.md §4.2。原样回传对应 ping 的 ts。</summary>
/// <param name="Ts">对应 ping 帧的 ts（UTC 毫秒）。</param>
public sealed record PongFrame(long Ts) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Pong;
}
