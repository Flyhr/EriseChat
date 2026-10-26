namespace Chat.Protocol.Frames;

/// <summary>
/// 好友/申请变更推送（S→C）：protocol/protocol.md §4.9。
/// 推送帧不落库，离线用户不补推；客户端按 <c>kind</c> 幂等更新本地状态。
/// </summary>
/// <param name="Kind">变更类型，见 <see cref="FriendChangedKinds"/>。</param>
/// <param name="Friend">好友对象快照（kind 为 friend.* / request.accepted 时携带）。</param>
/// <param name="Request">申请对象快照（kind 为 request.* 时携带）。</param>
public sealed record FriendChangedFrame(
    string Kind,
    Friend? Friend = null,
    FriendRequest? Request = null) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.FriendChanged;
}

/// <summary>friend.changed 的 kind 取值（protocol/protocol.md §4.9）。</summary>
public static class FriendChangedKinds
{
    /// <summary>收到新的好友申请（推给被申请人，携带 request）。</summary>
    public const string RequestReceived = "request.received";

    /// <summary>自己发出的申请被接受（推给申请人，携带 request + friend）。</summary>
    public const string RequestAccepted = "request.accepted";

    /// <summary>自己发出的申请被拒绝（推给申请人，携带 request）。</summary>
    public const string RequestRejected = "request.rejected";

    /// <summary>对方撤销了发给自己的申请（推给被申请人，携带 request）。</summary>
    public const string RequestCancelled = "request.cancelled";

    /// <summary>新增好友关系（推给双方，携带 friend）。</summary>
    public const string FriendAdded = "friend.added";

    /// <summary>好友关系被删除（推给双方，携带移除前 friend 快照）。</summary>
    public const string FriendRemoved = "friend.removed";

    /// <summary>自己的好友备注/分组变更（推给本人，携带 friend）。</summary>
    public const string FriendUpdated = "friend.updated";
}
