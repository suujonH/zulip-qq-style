using System;
using System.Text.Json.Nodes;

namespace ZulipQQ.Plugins;

/// <summary>插件面板的对象种类：用户资料卡、话题的资料面板。</summary>
public enum PluginTargetKind
{
    User,
    Topic,
}

/// <summary>
/// 插件面板的对象（zq_on_panel 的 target）：用户为 <c>{"account": 账号 id, "user": 用户 id, "name": 显示名}</c>，
/// 话题为 <c>{"account", "stream": 频道 id, "topic": 话题名, "name": "频道 / 话题"}</c>。
/// name 只用于显示（打开面板或选择时的名称，之后改名不会更新）。<see cref="AccountId"/> 为本地账号 id（与 zq_message 的 account_id 相同）。
/// </summary>
public sealed record PluginTarget(PluginTargetKind Kind, long AccountId, long UserId, long StreamId, string Topic, string Name)
{
    public static PluginTarget ForUser(long accountId, long userId, string name) =>
        new(PluginTargetKind.User, accountId, userId, 0, "", name);

    public static PluginTarget ForTopic(long accountId, long streamId, string topic, string name) =>
        new(PluginTargetKind.Topic, accountId, 0, streamId, topic, name);

    /// <summary>按种类解析；缺少账号、用户 / 频道或话题时返回 null。</summary>
    public static PluginTarget? From(JsonObject? obj, PluginTargetKind kind)
    {
        if (obj is null || Long(obj, "account") is not (> 0 and var account))
            return null;
        var name = obj["name"] is JsonValue n && n.TryGetValue<string>(out var s) ? s : "";
        if (kind == PluginTargetKind.User)
            return Long(obj, "user") is > 0 and var user ? ForUser(account, user, name) : null;
        var topic = obj["topic"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : "";
        return Long(obj, "stream") is > 0 and var stream && topic.Length > 0 ? ForTopic(account, stream, topic, name) : null;
    }

    public JsonObject ToJson() => Kind == PluginTargetKind.User
        ? new JsonObject { ["account"] = AccountId, ["user"] = UserId, ["name"] = Name }
        : new JsonObject { ["account"] = AccountId, ["stream"] = StreamId, ["topic"] = Topic, ["name"] = Name };

    /// <summary>同一个用户 / 话题（不比较名称；话题名与 Zulip 一样不区分大小写）。</summary>
    public bool SameAs(PluginTarget other) => Kind == other.Kind && AccountId == other.AccountId && (Kind == PluginTargetKind.User
        ? UserId == other.UserId
        : StreamId == other.StreamId && string.Equals(Topic, other.Topic, StringComparison.OrdinalIgnoreCase));

    private static long Long(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<long>(out var n) ? n : 0;
}
