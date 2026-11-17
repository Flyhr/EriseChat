using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// calls：1:1 通话记录（表结构唯一来源：protocol/schema.sql）。
/// SDP / ICE 只转发不落库；<see cref="State"/> 与 <see cref="EndReason"/> 由服务端状态机维护。
/// </summary>
public sealed class Call
{
    /// <summary>通话 ID（主叫客户端生成，UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属单聊会话 ID。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>主叫用户 ID（时间线记录的 sender_id 取此值）。</summary>
    public string CallerId { get; set; } = string.Empty;

    /// <summary>被叫用户 ID。</summary>
    public string CalleeId { get; set; } = string.Empty;

    /// <summary>通话类型：audio / video。</summary>
    public CallKind Kind { get; set; }

    /// <summary>状态：ringing / connecting / active / ended。</summary>
    public CallState State { get; set; }

    /// <summary>创建（邀请）时间，UTC 毫秒。</summary>
    public long CreatedAt { get; set; }

    /// <summary>被叫接听时间，UTC 毫秒；未接为 null。</summary>
    public long? AnsweredAt { get; set; }

    /// <summary>媒体接通时间（首个 call.connected），UTC 毫秒；未接通为 null。</summary>
    public long? ConnectedAt { get; set; }

    /// <summary>结束时间，UTC 毫秒。</summary>
    public long? EndedAt { get; set; }

    /// <summary>结束原因：hangup / timeout / canceled / answered_elsewhere / disconnected / failed / declined / unavailable。</summary>
    public string? EndReason { get; set; }
}
