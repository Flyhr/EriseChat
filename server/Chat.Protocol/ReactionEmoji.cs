namespace Chat.Protocol;

/// <summary>
/// 消息表情回应的稳定标识（C3）：协议只传标识，各端负责映射显示（Web/Windows/Android 各自渲染 emoji）。
/// 服务端以此白名单校验，未知标识返回 VALIDATION_FAILED；新增表情只追加常量与白名单，不改帧结构。
/// </summary>
public static class ReactionEmoji
{
    /// <summary>👍 赞同。</summary>
    public const string ThumbsUp = "thumbs_up";

    /// <summary>❤️ 喜欢。</summary>
    public const string Heart = "heart";

    /// <summary>😂 笑哭。</summary>
    public const string Joy = "joy";

    /// <summary>😮 惊讶。</summary>
    public const string Wow = "wow";

    /// <summary>😢 难过。</summary>
    public const string Sad = "sad";

    /// <summary>🎉 庆祝。</summary>
    public const string Tada = "tada";

    /// <summary>首发固定常用表情（顺序即菜单/回应条展示顺序）。</summary>
    public static readonly IReadOnlyList<string> All =
    [
        ThumbsUp,
        Heart,
        Joy,
        Wow,
        Sad,
        Tada,
    ];

    /// <summary>是否为受支持的回应标识（空或未登记均为否）。</summary>
    public static bool IsSupported(string? emoji) =>
        emoji is not null && All.Contains(emoji, StringComparer.Ordinal);
}
