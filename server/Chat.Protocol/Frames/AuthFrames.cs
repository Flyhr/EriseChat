namespace Chat.Protocol.Frames;

/// <summary>auth（C→S，连接后首帧）：protocol/protocol.md §4.1。</summary>
/// <param name="Token">REST 登录返回的 JWT accessToken。</param>
/// <param name="DeviceId">设备标识，1~128 字符；同一设备重连复用。</param>
/// <param name="ProtocolVersion">客户端协议版本，缺省视为 1。</param>
/// <param name="CallCapabilities">通话能力（protocol.md §3.1）：audio / video；省略表示不支持通话。</param>
public sealed record AuthFrame(
    string Token,
    string DeviceId,
    int? ProtocolVersion = null,
    IReadOnlyList<string>? CallCapabilities = null) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Auth;
}

/// <summary>hello（S→C，认证成功）：protocol/protocol.md §4.1。</summary>
/// <param name="UserId">当前登录用户 ID。</param>
/// <param name="HeartbeatSec">服务端建议心跳间隔（秒），当前为 25。</param>
/// <param name="ServerTime">服务端当前时间（UTC 毫秒），供客户端校准展示时间。</param>
/// <param name="ProtocolVersion">服务端协议版本，当前为 1。</param>
public sealed record HelloFrame(string UserId, int HeartbeatSec, long ServerTime, int ProtocolVersion) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Hello;
}
