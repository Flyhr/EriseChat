using Chat.Protocol;

namespace Chat.Server.Data.Entities;

/// <summary>
/// messages：消息。<see cref="Seq"/> 会话内严格单调 +1，服务端在事务内分配、落库后不可变；
/// (<see cref="ConversationId"/>, <see cref="ClientMsgId"/>) 唯一用于幂等发送。
/// </summary>
public sealed class Message
{
    /// <summary>消息 ID（UUID 文本）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>所属会话 ID。</summary>
    public string ConversationId { get; set; } = string.Empty;

    /// <summary>发送者用户 ID。</summary>
    public string SenderId { get; set; } = string.Empty;

    /// <summary>客户端生成的幂等键（UUID 文本）。</summary>
    public string ClientMsgId { get; set; } = string.Empty;

    /// <summary>会话内序号（严格 +1）。</summary>
    public long Seq { get; set; }

    /// <summary>消息类型：text / image / file / system。</summary>
    public MessageType MsgType { get; set; }

    /// <summary>按 msg_type 解释的 JSON 文本（text 为 JSON 字符串，其余为 JSON 对象）。</summary>
    public string ContentJson { get; set; } = string.Empty;

    /// <summary>
    /// image/file 消息引用的文件 ID（files.id，可空）。text/system 为 NULL。
    /// 支撑列：不建库级外键（SQLite 对已有表加 FK 需整表重建），引用完整性由应用层发送时校验、
    /// 孤儿清理兜底；用于文件下载时的会话成员鉴权与孤儿清理（M3-01，不属于帧字段）。
    /// </summary>
    public string? FileId { get; set; }

    /// <summary>被回复消息 ID（自引用，可空）。</summary>
    public string? ReplyTo { get; set; }

    /// <summary>创建时间（UTC 毫秒）。</summary>
    public long CreatedAt { get; set; }

    /// <summary>删除时间（预留：v1 不做撤回，恒为 NULL；P2 撤回使用）。</summary>
    public long? DeletedAt { get; set; }
}
