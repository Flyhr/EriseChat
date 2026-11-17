namespace Chat.Protocol.Frames;

/// <summary>
/// 1:1 通话信令帧（protocol/protocol.md §3.1）。
/// 约定：<c>callId</c> 由主叫客户端生成（UUID 文本），后续帧沿用；
/// SDP/ICE 只经服务端转发，不落库、不写日志。
/// </summary>
/// <param name="CallId">通话音符串（主叫生成，≤64 字符）。</param>
public abstract record CallFrameBase(string CallId) : FrameEnvelope;

/// <summary>通话对端摘要（来电/记录展示用）。</summary>
public sealed record CallPeer(string UserId, string DisplayName, string? AvatarFileId = null);

/// <summary>call.invite（C→S）：发起 1:1 通话。会话必须是单聊。</summary>
/// <param name="CallId">主叫生成的通话 ID。</param>
/// <param name="ConversationId">单聊会话 ID。</param>
/// <param name="Kind">audio / video。</param>
public sealed record CallInviteFrame(string CallId, string ConversationId, string Kind) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallInvite;
}

/// <summary>call.incoming（S→C）：来电推送；被叫没有可接听的设备时 <see cref="CallSupported"/> 为 false。</summary>
public sealed record CallIncomingFrame(
    string CallId,
    string ConversationId,
    string Kind,
    CallPeer Peer,
    long CreatedAt) : CallFrameBase(CallId)
{
    /// <summary>本连接能否接听该类型通话；false 时客户端只提示“请在其他设备接听”。</summary>
    public bool? CallSupported { get; init; }

    /// <inheritdoc />
    public override string Type => FrameTypes.CallIncoming;
}

/// <summary>call.ringing（C→S / S→C）：被叫端已显示来电；服务端转发给主叫。</summary>
public sealed record CallRingingFrame(string CallId) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallRinging;
}

/// <summary>call.accept（C→S）：接听。首个 accept 生效。</summary>
public sealed record CallAcceptFrame(string CallId) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallAccept;
}

/// <summary>call.accepted（S→C）：接听确认，触发 SDP/ICE 交换。</summary>
public sealed record CallAcceptedFrame(string CallId, CallPeer Peer, long AnsweredAt) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallAccepted;
}

/// <summary>call.connected（C→S）：本端首次收到远端媒体。服务端按 callId 只记一次。</summary>
public sealed record CallConnectedFrame(string CallId) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallConnected;
}

/// <summary>call.reject（C→S）：拒接。</summary>
/// <param name="CallId">通话 ID。</param>
/// <param name="Reason">declined / busy / unavailable / no_media。</param>
public sealed record CallRejectFrame(string CallId, string? Reason = null) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallReject;
}

/// <summary>call.cancel（C→S / S→C）：取消（主叫主动取消、超时或他人已接）。</summary>
public sealed record CallCancelFrame(string CallId, string? Reason = null) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallCancel;
}

/// <summary>call.end（C→S / S→C）：挂断。任一端发起，服务端清理并广播。</summary>
/// <param name="CallId">通话 ID。</param>
/// <param name="Reason">hangup / timeout / canceled / answered_elsewhere / disconnected / failed。</param>
public sealed record CallEndFrame(string CallId, string? Reason = null) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallEnd;
}

/// <summary>SDP 内容（offer/answer 原样转发）。</summary>
public sealed record CallSdpPayload(string Type, string Sdp);

/// <summary>call.sdp（双向）：SDP 交换，只转发给对端连接。</summary>
public sealed record CallSdpFrame(string CallId, CallSdpPayload Sdp) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallSdp;
}

/// <summary>ICE candidate（字段与 RTCIceCandidateInit 对齐，全部可空以支持结束标记）。</summary>
public sealed record CallIcePayload(
    string? Candidate,
    string? SdpMid = null,
    int? SdpMLineIndex = null,
    string? UsernameFragment = null);

/// <summary>call.ice（双向）：ICE candidate 交换，只转发、不落库、不写日志。</summary>
public sealed record CallIceFrame(string CallId, CallIcePayload Candidate) : CallFrameBase(CallId)
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallIce;
}

/// <summary>
/// call.state（S→C）：重连同步当前通话；<see cref="CallId"/> 为 null 表示没有进行中的通话。
/// </summary>
public sealed record CallStateFrame(
    string? CallId,
    string? State = null,
    string? Kind = null,
    CallPeer? Peer = null,
    long? CreatedAt = null,
    long? AnsweredAt = null,
    long? ConnectedAt = null,
    string? Direction = null) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.CallState;
}
