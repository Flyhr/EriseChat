using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// friends：好友关系（F-17，单向行，主键 owner_user_id + friend_user_id）。
/// 互为好友时双方各一行，由服务端事务维护；删除好友时删除双向两行。
/// </summary>
public sealed class Friend
{
    /// <summary>关系拥有者（「我的好友」列表归属）。</summary>
    public string OwnerUserId { get; set; } = string.Empty;

    /// <summary>好友用户 ID。</summary>
    public string FriendUserId { get; set; } = string.Empty;

    /// <summary>我对该好友的备注（空表示无备注）。</summary>
    public string? Remark { get; set; }

    /// <summary>我对该好友的分组名（空表示未分组）。</summary>
    public string? GroupName { get; set; }

    /// <summary>成为好友的时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
