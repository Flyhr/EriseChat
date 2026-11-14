namespace Chat.Server.Data.Entities;

/// <summary>
/// message_reactions：消息表情回应（C3）。(<see cref="MessageId"/>, <see cref="UserId"/>) 复合主键，
/// 保证每人对每条消息至多一条回应；选择其他表情即替换本行，取消即删除本行。
/// 回应不产生消息、不推进 seq/已读，只影响消息的回应摘要与 <see cref="Message.ReactionsUpdatedAt"/>。
/// </summary>
public sealed class MessageReaction
{
    /// <summary>被回应消息 ID（messages.id）。</summary>
    public string MessageId { get; set; } = string.Empty;

    /// <summary>回应者用户 ID。</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>稳定表情标识（见 Chat.Protocol.ReactionEmoji 白名单）。</summary>
    public string Emoji { get; set; } = string.Empty;

    /// <summary>首次回应时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>最近一次变更（替换表情）时间（UTC 毫秒）。</summary>
    public long UpdatedAt { get; set; }
}
