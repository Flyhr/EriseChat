using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Chat.Protocol.Frames;

namespace Chat.Server.Realtime;

/// <summary>WebSocket 关闭码（唯一来源 protocol/protocol.md §4.1：1000/1001/1009/4001/4002）。</summary>
public static class WsCloseCodes
{
    /// <summary>正常关闭。</summary>
    public const int Normal = 1000;

    /// <summary>服务端/客户端离开。</summary>
    public const int GoingAway = 1001;

    /// <summary>帧超过 256 KiB。</summary>
    public const int MessageTooBig = 1009;

    /// <summary>被新连接踢下线（先发 kicked 帧）。</summary>
    public const int Kicked = 4001;

    /// <summary>协议错误（非法 JSON/缺 type/认证失败/认证超时）。</summary>
    public const int ProtocolError = 4002;
}

/// <summary>接收结果类型。</summary>
public enum FrameReceiveKind
{
    /// <summary>收到完整文本帧（一行一帧）。</summary>
    Text,

    /// <summary>收到二进制帧：协议不使用，整条消息丢弃后继续（记 debug）。</summary>
    BinaryIgnored,

    /// <summary>单帧超过 256 KiB 上限：调用方应发送错误并关闭 1009。</summary>
    TooLarge,

    /// <summary>客户端发送了 close 帧（或连接已关闭）。</summary>
    Closed,
}

/// <summary>接收结果（文本帧的 Json 已按 UTF-8 解码，不含换行）。</summary>
public readonly record struct FrameReceiveResult(
    FrameReceiveKind Kind,
    string? Json = null,
    WebSocketCloseStatus? CloseStatus = null,
    string? CloseDescription = null)
{
    public static FrameReceiveResult Text(string json) => new(FrameReceiveKind.Text, json);

    public static FrameReceiveResult BinaryIgnored => new(FrameReceiveKind.BinaryIgnored);

    public static FrameReceiveResult TooLarge => new(FrameReceiveKind.TooLarge);

    public static FrameReceiveResult Closed(WebSocketCloseStatus? status, string? description) =>
        new(FrameReceiveKind.Closed, null, status, description);
}

/// <summary>
/// 帧编解码薄封装（docs/project-overview.md §7.5：Realtime/FrameCodec.cs 只做编解码）。
/// DTO 与 JSON 序列化唯一实现位于 <c>Chat.Protocol.Frames</c>；本类附加 WebSocket 传输约定：
/// 一行一帧（JSON + '\n'）、只收发 UTF-8 文本帧、单帧上限 256 KiB、二进制帧忽略。
/// </summary>
internal static class FrameCodec
{
    /// <summary>单帧上限 256 KiB（协议 §2：超过则由服务端关闭 1009）。</summary>
    public const int MaxFrameBytes = 256 * 1024;

    private const int ReceiveBufferSize = 8 * 1024;

    /// <summary>统一 JSON 序列化选项（camelCase、忽略未知字段、枚举小写字符串、不缩进）。</summary>
    public static JsonSerializerOptions JsonOptions => Chat.Protocol.Frames.FrameCodec.JsonOptions;

    /// <summary>按运行时类型序列化为单行 JSON。</summary>
    public static string Serialize(FrameEnvelope frame) => Chat.Protocol.Frames.FrameCodec.Serialize(frame);

    /// <summary>解析单帧 JSON；未知 type 返回 <c>UnknownType</c>，不抛异常。</summary>
    public static FrameParseResult Parse(string json) => Chat.Protocol.Frames.FrameCodec.Parse(json);

    /// <summary>发送一帧（JSON + 换行符，文本帧）。</summary>
    public static Task SendFrameAsync(WebSocket socket, FrameEnvelope frame, CancellationToken cancellationToken) =>
        SendTextAsync(socket, Serialize(frame), cancellationToken);

    /// <summary>发送一段已序列化的 JSON（自动追加换行符，文本帧）。</summary>
    public static async Task SendTextAsync(WebSocket socket, string json, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes(json + "\n");
        await socket.SendAsync(payload, WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    /// <summary>接收一条完整消息（自动聚合分片）。二进制帧整条丢弃、超限返回 TooLarge、close 帧返回 Closed。</summary>
    public static async Task<FrameReceiveResult> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[ReceiveBufferSize];
        var message = new MemoryStream();
        while (true)
        {
            var received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (received.MessageType == WebSocketMessageType.Close)
            {
                return FrameReceiveResult.Closed(received.CloseStatus, received.CloseStatusDescription);
            }

            if (received.MessageType == WebSocketMessageType.Binary)
            {
                // 二进制帧不属于 JSON 协议：整条消息读完丢弃，由调用方记 debug 后继续。
                if (received.EndOfMessage)
                {
                    return FrameReceiveResult.BinaryIgnored;
                }

                continue;
            }

            if (message.Length + received.Count > MaxFrameBytes)
            {
                return FrameReceiveResult.TooLarge;
            }

            message.Write(buffer, 0, received.Count);
            if (received.EndOfMessage)
            {
                return FrameReceiveResult.Text(Encoding.UTF8.GetString(message.ToArray()));
            }
        }
    }
}
