using System.Text.Json;
using System.Text.Json.Serialization;

namespace Chat.Protocol.Frames;

/// <summary>图片/文件消息内容（protocol/protocol.md §7）。二进制走 REST，WS 只传元数据。</summary>
/// <param name="FileId">文件 ID（files.id）。</param>
/// <param name="Name">原始文件名。</param>
/// <param name="Size">文件大小（字节）。</param>
/// <param name="Mime">MIME 类型。</param>
public sealed record FileContent(string FileId, string Name, long Size, string Mime);

/// <summary>
/// 系统消息内容（服务端生成，M3 使用；protocol/protocol.md §4.3）。
/// 后续新增可选字段属向后兼容增量。
/// </summary>
/// <param name="Event">事件名（必填）。</param>
/// <param name="ActorId">触发者用户 ID（可选）。</param>
/// <param name="TargetUserIds">涉及成员用户 ID（可选）。</param>
/// <param name="Text">服务端生成的中文描述（可选）。</param>
public sealed record SystemContent(
    string Event,
    string? ActorId = null,
    IReadOnlyList<string>? TargetUserIds = null,
    string? Text = null);

/// <summary>
/// 回复引用摘要（protocol/protocol.md §4.3 Reply 对象；不落库、按需由被引用消息组装）。
/// 文本摘要由服务端截断到 120 字符（超出追加省略号）；image/file 不返回 text、改带 fileId + name。
/// </summary>
/// <param name="MessageId">被引用消息 ID（= 发送时的 replyTo）。</param>
/// <param name="Seq">被引用消息在会话内的序号。</param>
/// <param name="SenderId">被引用消息的发送者用户 ID。</param>
/// <param name="MsgType">被引用消息类型。</param>
/// <param name="Text">文本摘要（text / system）；image / file 为 null。</param>
/// <param name="FileId">image / file 消息的文件 ID。</param>
/// <param name="Name">image / file 消息的文件名。</param>
public sealed record MessageReply(
    string MessageId,
    long Seq,
    string SenderId,
    MessageType MsgType,
    string? Text = null,
    string? FileId = null,
    string? Name = null);

/// <summary>
/// 消息 content：按 <c>msgType</c> 保留「字符串 / 对象」两态
/// （text 为字符串；image/file 为 <see cref="FileContent"/>；system 为对象）。
/// 序列化时原样写出原始 JSON，不做二次转换。
/// </summary>
[JsonConverter(typeof(MessageContentJsonConverter))]
public readonly struct MessageContent : IEquatable<MessageContent>
{
    private readonly JsonElement _element;

    private MessageContent(JsonElement element) => _element = element;

    /// <summary>value 是否缺失（未赋值或 JSON 中为 null）。</summary>
    public bool IsEmpty => _element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null;

    /// <summary>是否为字符串内容（text 消息）。</summary>
    public bool IsString => _element.ValueKind == JsonValueKind.String;

    /// <summary>是否为对象内容（image/file/system 消息）。</summary>
    public bool IsObject => _element.ValueKind == JsonValueKind.Object;

    /// <summary>原始 JSON（未赋值时为 default）。</summary>
    public JsonElement Raw => IsEmpty ? default : _element.Clone();

    /// <summary>由字符串内容构造（text 消息）。</summary>
    public static MessageContent FromString(string value) =>
        new(JsonSerializer.SerializeToElement(value, FrameCodec.JsonOptions));

    /// <summary>由对象内容构造（如 <see cref="FileContent"/> / <see cref="SystemContent"/>）。</summary>
    public static MessageContent FromObject<T>(T value) where T : notnull =>
        new(JsonSerializer.SerializeToElement(value, FrameCodec.JsonOptions));

    /// <summary>由已解析的 JSON 元素构造（会克隆，脱离原 JsonDocument 生命周期）。</summary>
    public static MessageContent FromElement(JsonElement element) => new(element.Clone());

    /// <summary>取字符串内容；非字符串返回 null。</summary>
    public string? GetString() => IsString ? _element.GetString() : null;

    /// <summary>按 <see cref="FileContent"/> 解析对象内容；非对象返回 null。</summary>
    public FileContent? AsFileContent() => IsObject ? _element.Deserialize<FileContent>(FrameCodec.JsonOptions) : null;

    /// <summary>按指定类型解析对象内容；非对象返回 default。</summary>
    public T? As<T>() => IsObject ? _element.Deserialize<T>(FrameCodec.JsonOptions) : default;

    /// <inheritdoc />
    public bool Equals(MessageContent other) =>
        string.Equals(IsEmpty ? null : _element.GetRawText(), other.IsEmpty ? null : other._element.GetRawText(), StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MessageContent other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => IsEmpty ? 0 : StringComparer.Ordinal.GetHashCode(_element.GetRawText());
}

/// <summary>
/// <see cref="MessageContent"/> 的 JSON 转换器：写入原始 JSON，读取时克隆为独立 JSON 元素。
/// 未赋值（默认值）序列化为 null，避免访问未定义状态。
/// </summary>
internal sealed class MessageContentJsonConverter : JsonConverter<MessageContent>
{
    public override MessageContent Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return MessageContent.FromElement(document.RootElement);
    }

    public override void Write(Utf8JsonWriter writer, MessageContent value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (value.IsEmpty)
        {
            writer.WriteNullValue();
            return;
        }

        value.Raw.WriteTo(writer);
    }
}

/// <summary>消息对象（protocol/protocol.md §4.3）。</summary>
/// <param name="MessageId">消息 ID（messages.id）。</param>
/// <param name="ConversationId">会话 ID。</param>
/// <param name="SenderId">发送者用户 ID。</param>
/// <param name="Seq">会话内序号。</param>
/// <param name="MsgType">消息类型。</param>
/// <param name="Content">按 msgType 解释的内容，见 <see cref="MessageContent"/>。</param>
/// <param name="CreatedAt">创建时间（UTC 毫秒）。</param>
/// <param name="ClientMsgId">发送者客户端幂等键；服务端广播时可不带。</param>
/// <param name="ReplyTo">被回复消息的 messageId。</param>
/// <param name="DeletedAt">预留（v1 不做撤回，恒为空）。</param>
/// <param name="Reply">回复引用摘要（可选，F-09）：仅当 replyTo 指向的消息存在时返回。</param>
public sealed record Message(
    string MessageId,
    string ConversationId,
    string SenderId,
    long Seq,
    MessageType MsgType,
    MessageContent Content,
    long CreatedAt,
    string? ClientMsgId = null,
    string? ReplyTo = null,
    long? DeletedAt = null,
    MessageReply? Reply = null);
