namespace Chat.Protocol.Frames;

/// <summary>
/// WebSocket 帧 type 常量（唯一来源：protocol/protocol.md §3，共 22 个）。
/// 帧 DTO 与处理逻辑在 M1 按协议补全；F-18 追加消息撤回/编辑 4 个 type。
/// </summary>
public static class FrameTypes
{
    /// <summary>首帧认证（C→S）。</summary>
    public const string Auth = "auth";

    /// <summary>认证成功（S→C）。</summary>
    public const string Hello = "hello";

    /// <summary>心跳请求（双向）。</summary>
    public const string Ping = "ping";

    /// <summary>心跳响应（双向）。</summary>
    public const string Pong = "pong";

    /// <summary>发送消息（C→S）。</summary>
    public const string MessageSend = "message.send";

    /// <summary>落库回执（S→C，仅发送者）。</summary>
    public const string MessageAck = "message.ack";

    /// <summary>新消息广播（S→C）。</summary>
    public const string MessageNew = "message.new";

    /// <summary>撤回消息请求（C→S，F-18）。</summary>
    public const string MessageRecall = "message.recall";

    /// <summary>撤回消息广播/回执（S→C，F-18）。</summary>
    public const string MessageRecalled = "message.recalled";

    /// <summary>编辑消息请求（C→S，F-18）。</summary>
    public const string MessageEdit = "message.edit";

    /// <summary>编辑消息广播/回执（S→C，F-18）。</summary>
    public const string MessageEdited = "message.edited";

    /// <summary>补拉请求（C→S）。</summary>
    public const string SyncRequest = "sync.request";

    /// <summary>补拉响应（S→C）。</summary>
    public const string SyncResponse = "sync.response";

    /// <summary>会话列表请求/响应（双向）。</summary>
    public const string ConversationList = "conv.list";

    /// <summary>会话变更推送（S→C）。</summary>
    public const string ConversationUpdated = "conv.updated";

    /// <summary>上报已读游标（C→S）。</summary>
    public const string ReadUpdate = "read.update";

    /// <summary>已读广播（S→C，仅单聊）。</summary>
    public const string ReadUpdated = "read.updated";

    /// <summary>正在输入（双向）。</summary>
    public const string Typing = "typing";

    /// <summary>在线状态（S→C）。</summary>
    public const string Presence = "presence";

    /// <summary>个人收藏变更推送（S→C，F-38）：同账号全部在线连接。</summary>
    public const string FavoriteChanged = "favorite.changed";

    /// <summary>好友/申请变更推送（S→C，F-17）。</summary>
    public const string FriendChanged = "friend.changed";

    /// <summary>被踢下线（S→C）。</summary>
    public const string Kicked = "kicked";

    /// <summary>统一错误（S→C）。</summary>
    public const string Error = "error";
}
