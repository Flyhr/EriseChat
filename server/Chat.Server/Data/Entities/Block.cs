namespace Chat.Server.Data.Entities;

/// <summary>
/// blocks：拉黑关系（C1b，有方向的单向行，主键 blocker_user_id + blocked_user_id）。
/// 任一方拉黑期间双方不能私聊、转发到对方私聊、再次申请好友或直接邀请对方入群；
/// 共同群消息与历史消息读取不受影响。拉黑不改变好友关系，解除拉黑只删除对应行。
/// </summary>
public sealed class Block
{
    /// <summary>发起拉黑的用户 ID（「我拉黑了谁」）。</summary>
    public string BlockerUserId { get; set; } = string.Empty;

    /// <summary>被拉黑的用户 ID。</summary>
    public string BlockedUserId { get; set; } = string.Empty;

    /// <summary>拉黑时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
