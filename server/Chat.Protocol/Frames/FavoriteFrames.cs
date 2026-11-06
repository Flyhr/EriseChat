namespace Chat.Protocol.Frames;

/// <summary>
/// 个人收藏变更推送（S→C，F-38，protocol/protocol.md §4.10）：
/// 当前账号新增/取消收藏后推送给本人全部在线连接，客户端据此重新拉取收藏列表。
/// </summary>
/// <param name="MessageId">发生变化的原消息 ID。</param>
/// <param name="Favorited">true=新增收藏；false=取消收藏。</param>
/// <param name="ChangedAt">变更时间（UTC 毫秒）。</param>
public sealed record FavoriteChangedFrame(string MessageId, bool Favorited, long ChangedAt) : FrameEnvelope
{
    public override string Type => FrameTypes.FavoriteChanged;
}
