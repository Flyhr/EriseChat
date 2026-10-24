namespace Chat.Protocol.Frames;

/// <summary>
/// WebSocket 帧 DTO 基类（唯一契约来源：protocol/protocol.md §2/§4）。
/// 每个 type 的具体 DTO 位于本目录；Chat.Server 的 Realtime/FrameCodec.cs 只做编解码薄封装。
/// 帧 DTO 使用 record 以便值语义与不可变；基类必须是 record 才能被 record 继承。
/// </summary>
public abstract record FrameEnvelope
{
    /// <summary>
    /// 帧类型（JSON 字段 <c>type</c>），由各 DTO 固定返回，取值见 <see cref="FrameTypes"/>。
    /// 反序列化时该字段只用于分派，不写入实例。
    /// </summary>
    public abstract string Type { get; }

    /// <summary>
    /// 请求/响应关联 ID（JSON 字段 <c>reqId</c>，可选，1~64 字符）。
    /// C→S 请求携带时，服务端在对应响应帧中原样回传；S→C 主动推送不带。
    /// </summary>
    public string? ReqId { get; init; }
}
