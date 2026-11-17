namespace Chat.Server.Common.Options;

/// <summary>1:1 通话相关配置（appsettings 节点 <c>Call</c>）：protocol/protocol.md §3.1。</summary>
public sealed class CallOptions
{
    /// <summary>配置节名称。</summary>
    public const string SectionName = "Call";

    /// <summary>是否启用通话信令；关闭时 call.* 帧回 CALL_UNSUPPORTED。</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>振铃超时（秒）：被叫无应答时服务端下发 call.end(timeout)。</summary>
    public int RingTimeoutSeconds { get; set; } = 60;

    /// <summary>连接断开后保留通话的宽限（秒）：重连可收 call.state 恢复。</summary>
    public int DisconnectGraceSeconds { get; set; } = 10;

    /// <summary>单次通话最长时长（秒），到点强制结束；0 表示不限。</summary>
    public int MaxDurationSeconds { get; set; } = 0;
}
