using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ZulipQQ.Core;

namespace ZulipQQ.Zulip;

/// <summary>narrow 的一项，序列化为 <c>{"operator":…,"operand":…,"negated":…}</c>。</summary>
public sealed record NarrowTerm(
    [property: JsonPropertyName("operator")] string Operator,
    [property: JsonPropertyName("operand")] JsonNode Operand,
    [property: JsonPropertyName("negated")] bool Negated = false);

/// <summary>构造 narrow 的工厂方法。</summary>
public static class Narrow
{
    public static NarrowTerm Channel(long id) => new("channel", JsonValue.Create(id));

    public static NarrowTerm Topic(string topic) => new("topic", JsonValue.Create(topic));

    public static NarrowTerm Dm(IEnumerable<long> userIds) =>
        new("dm", new JsonArray(userIds.Select(id => (JsonNode?)JsonValue.Create(id)).ToArray()));

    /// <summary>如 <c>starred</c>、<c>unread</c>、<c>mentioned</c>。</summary>
    public static NarrowTerm Is(string value) => new("is", JsonValue.Create(value));

    /// <summary>如 <c>attachment</c>、<c>image</c>、<c>link</c>。</summary>
    public static NarrowTerm Has(string value, bool negated = false) => new("has", JsonValue.Create(value), negated);

    public static NarrowTerm Search(string text) => new("search", JsonValue.Create(text));

    public static NarrowTerm Sender(long userId) => new("sender", JsonValue.Create(userId));

    /// <summary>
    /// 某个会话的 narrow。话题使用 <paramref name="topicName"/>（原始大小写，由调用方传入）；
    /// 与自己的私聊为 <c>dm:[自己]</c>；其它私聊为 <c>dm:[对方 id…]</c>，不含自己。
    /// 已在 Zulip 12.3（FL 500）上验证：多人私聊的 operand 含不含自己，匹配结果相同。
    /// </summary>
    public static List<NarrowTerm> For(ConversationKey key, long selfId, string? topicName = null)
    {
        return key switch
        {
            TopicKey t => [Channel(t.StreamId), Topic(topicName ?? t.Topic)],
            DmKey { IsSelfDm: true } => [Dm([selfId])],
            DmKey d => [Dm(d.OtherUserIds)],
            _ => [],
        };
    }
}
