using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Protocol.Frames;

/// <summary>帧解析结果状态。</summary>
public enum FrameParseStatus
{
    /// <summary>解析成功，<see cref="FrameParseResult.Frame"/> 非空。</summary>
    Success,

    /// <summary>
    /// 帧是合法 JSON 且带 <c>type</c>，但 type 未知。按 protocol/protocol.md §2/§8.2
    /// 调用方应记 debug 日志后忽略该帧，不断连、不回 error。
    /// </summary>
    UnknownType,

    /// <summary>
    /// 非法 JSON、缺少 <c>type</c> 或字段类型错误。调用方应回 <c>error</c>（BAD_FRAME）并关闭 4002。
    /// </summary>
    Invalid,
}

/// <summary>帧解析结果。<see cref="Error"/> 为便于排查的英文说明，不直接下发客户端。</summary>
public readonly record struct FrameParseResult(FrameParseStatus Status, FrameEnvelope? Frame, string? Error)
{
    /// <summary>是否解析成功。</summary>
    public bool IsSuccess => Status == FrameParseStatus.Success;
}

/// <summary>
/// 帧编解码唯一实现（Chat.Server/Realtime/FrameCodec.cs 为薄封装）。
/// 统一序列化选项：camelCase、不缩进（一行一帧）、忽略未知字段、空值字段不输出、枚举小写字符串。
/// </summary>
public static class FrameCodec
{
    /// <summary>所有帧共用的 JSON 序列化选项。</summary>
    public static JsonSerializerOptions JsonOptions { get; } = CreateJsonOptions();

    private static JsonSerializerOptions CreateJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            // JSON 字段统一 camelCase；与 apps/web/src/types/frames.d.ts 保持一致。
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            // 可选字段为 null 时不输出（TS 中为可选字段）。
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            // 未知字段忽略（protocol.md §2/§8.2 向后兼容要求）。
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
            // 一行一帧：不缩进；序列化结果不含换行。
            WriteIndented = false,
        };
        // 枚举以协议中的小写字符串形式传输（如 text / direct / owner）。
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false));
        return options;
    }

    /// <summary>按运行时类型序列化为单行 JSON（不含缩进与换行）。</summary>
    public static string Serialize(FrameEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        return JsonSerializer.Serialize(frame, frame.GetType(), JsonOptions);
    }

    /// <summary>序列化到已有的 <see cref="Utf8JsonWriter"/>（供 WS 发送缓冲复用）。</summary>
    public static void Serialize(Utf8JsonWriter writer, FrameEnvelope frame)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(frame);
        JsonSerializer.Serialize(writer, frame, frame.GetType(), JsonOptions);
    }

    /// <summary>
    /// 解析一帧 JSON。空串、非法 JSON、缺少 type、字段类型错误返回 <see cref="FrameParseStatus.Invalid"/>；
    /// 未知 type 返回 <see cref="FrameParseStatus.UnknownType"/>（不抛异常）；两者均不抛出。
    /// </summary>
    public static FrameParseResult Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Invalid("empty frame");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            return Invalid($"invalid json: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return Invalid("frame must be a JSON object");
            }

            if (!root.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
            {
                return Invalid("missing or non-string field: type");
            }

            var type = typeElement.GetString();
            if (string.IsNullOrEmpty(type))
            {
                return Invalid("empty field: type");
            }

            try
            {
                var frame = DeserializeByType(type, root);
                return frame is null
                    ? new FrameParseResult(FrameParseStatus.UnknownType, null, $"unknown frame type: {type}")
                    : new FrameParseResult(FrameParseStatus.Success, frame, null);
            }
            catch (JsonException ex)
            {
                return Invalid(ex.Message);
            }
            catch (NotSupportedException ex)
            {
                return Invalid(ex.Message);
            }
        }
    }

    /// <summary>按已读取的 type 分派具体 DTO；未知 type 返回 null。</summary>
    private static FrameEnvelope? DeserializeByType(string type, JsonElement element) => type switch
    {
        FrameTypes.Auth => element.Deserialize<AuthFrame>(JsonOptions),
        FrameTypes.Hello => element.Deserialize<HelloFrame>(JsonOptions),
        FrameTypes.Ping => element.Deserialize<PingFrame>(JsonOptions),
        FrameTypes.Pong => element.Deserialize<PongFrame>(JsonOptions),
        FrameTypes.MessageSend => element.Deserialize<MessageSendFrame>(JsonOptions),
        FrameTypes.MessageAck => element.Deserialize<MessageAckFrame>(JsonOptions),
        FrameTypes.MessageNew => element.Deserialize<MessageNewFrame>(JsonOptions),
        FrameTypes.SyncRequest => element.Deserialize<SyncRequestFrame>(JsonOptions),
        FrameTypes.SyncResponse => element.Deserialize<SyncResponseFrame>(JsonOptions),
        // conv.list 双向同 type：S→C 响应带 conversations，C→S 请求不带（protocol.md §4.5）。
        FrameTypes.ConversationList => element.TryGetProperty("conversations", out _)
            ? element.Deserialize<ConvListResponseFrame>(JsonOptions)
            : element.Deserialize<ConvListRequestFrame>(JsonOptions),
        FrameTypes.ConversationUpdated => element.Deserialize<ConvUpdatedFrame>(JsonOptions),
        FrameTypes.ReadUpdate => element.Deserialize<ReadUpdateFrame>(JsonOptions),
        FrameTypes.ReadUpdated => element.Deserialize<ReadUpdatedFrame>(JsonOptions),
        // typing 双向同 type：S→C 推送带 userId（protocol.md §4.7）。
        FrameTypes.Typing => element.TryGetProperty("userId", out _)
            ? element.Deserialize<TypingServerFrame>(JsonOptions)
            : element.Deserialize<TypingClientFrame>(JsonOptions),
        FrameTypes.Presence => element.Deserialize<PresenceFrame>(JsonOptions),
        // F-17：好友/申请变更推送（S→C），不落库。
        FrameTypes.FriendChanged => element.Deserialize<FriendChangedFrame>(JsonOptions),
        FrameTypes.Kicked => element.Deserialize<KickedFrame>(JsonOptions),
        FrameTypes.Error => element.Deserialize<ErrorFrame>(JsonOptions),
        _ => null,
    };

    private static FrameParseResult Invalid(string error) => new(FrameParseStatus.Invalid, null, error);
}
