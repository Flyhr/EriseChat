using System.Text.Json;
using Chat.Protocol.Frames;

namespace Chat.Server.Realtime;

/// <summary>
/// 帧编解码薄封装（docs/project-overview.md §7.5：Realtime/FrameCodec.cs 只做编解码）。
/// DTO 与编解码唯一实现位于 <c>Chat.Protocol.Frames</c>，本类只暴露 Realtime 层需要的入口。
/// </summary>
internal static class FrameCodec
{
    /// <summary>统一 JSON 序列化选项（camelCase、一行一帧、忽略未知字段、枚举小写字符串）。</summary>
    public static JsonSerializerOptions JsonOptions => Chat.Protocol.Frames.FrameCodec.JsonOptions;

    /// <summary>按运行时类型序列化为单行 JSON。</summary>
    public static string Serialize(FrameEnvelope frame) => Chat.Protocol.Frames.FrameCodec.Serialize(frame);

    /// <summary>解析单帧 JSON；未知 type 返回 <c>UnknownType</c>，不抛异常。</summary>
    public static FrameParseResult Parse(string json) => Chat.Protocol.Frames.FrameCodec.Parse(json);
}
