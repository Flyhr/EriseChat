namespace Chat.Protocol.Frames;

/// <summary>
/// 帧 DTO 基类占位（M0-03 只留骨架）。
/// M1 按 protocol/protocol.md §4 为每个 type 补全具体 DTO（record/class），
/// JSON 序列化统一使用 camelCase，未知字段忽略、未知 type 忽略且不断连。
/// </summary>
public abstract class FrameEnvelope
{
    /// <summary>帧类型，取值见 <see cref="FrameTypes"/>。</summary>
    public required string Type { get; init; }
}
