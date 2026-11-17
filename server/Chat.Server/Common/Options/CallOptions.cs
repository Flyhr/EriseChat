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

    /// <summary>
    /// TURN 服务地址（例如 <c>turn:100.115.90.9:3478?transport=udp</c>）。
    /// 为空表示不下发 TURN，客户端只在局域网/直连场景可用。
    /// </summary>
    public string[] TurnUrls { get; set; } = [];

    /// <summary>
    /// coturn 的 <c>static-auth-secret</c>：服务端用它签发短期凭证（RFC 5766 的 REST API 方式）。
    /// 必须与 <c>coturn</c> 容器读取的配置一致；为空则 <c>/api/calls/ice-servers</c> 返回空列表。
    /// </summary>
    public string TurnSharedSecret { get; set; } = "";

    /// <summary>TURN 凭证有效期（秒），默认 1 小时。</summary>
    public int TurnCredentialTtlSeconds { get; set; } = 3600;
}
