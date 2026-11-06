namespace Chat.Server.Data.Entities;

/// <summary>
/// favorites：个人收藏（F-38，跨设备同步）。只保存账号 + 原消息引用 + 收藏时间，不复制消息内容；
/// 原消息编辑/撤回在读取时反映。主键 (user_id, message_id) 保证同账号对同一消息只收藏一次。
/// </summary>
public sealed class Favorite
{
    /// <summary>收藏归属账号。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>原消息 ID（messages.id）。</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>收藏时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }
}
