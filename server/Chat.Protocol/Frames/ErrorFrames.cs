namespace Chat.Protocol.Frames;

/// <summary>error（S→C，统一错误）：protocol/protocol.md §4.8/§5。</summary>
/// <param name="Code">错误码，取值见 <c>Chat.Protocol.ErrorCodes</c>；未知错误码客户端按通用错误处理。</param>
/// <param name="Message">可展示的中文说明（不包含敏感信息）。</param>
public sealed record ErrorFrame(string Code, string Message) : FrameEnvelope
{
    /// <inheritdoc />
    public override string Type => FrameTypes.Error;
}
