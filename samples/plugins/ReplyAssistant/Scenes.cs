using System.Linq;
using System.Text.Json.Nodes;
using PluginShared;

namespace ReplyAssistant;

/// <summary>
/// 决定分析一条消息时使用的场景与关系：话题决定场景（没有设置时为默认场景），发送者的身份属于这个场景时才使用它的关系，
/// 否则不指定关系；私聊使用对方的身份（及其场景），对方没有身份时使用默认身份，也没有默认身份时为默认场景、不指定关系。
/// 身份写作“场景 id/关系 id”，与 plugin.json 中 default_identity 的选项一致。
/// </summary>
internal static class Scenes
{
    public const string DefaultSceneKey = "default_scene";
    public const string DefaultIdentityKey = "default_identity";

    public static SceneChoice Resolve(MessageInfo message, JsonObject settings)
    {
        var defaultScene = DefaultScene(settings);
        if (message.ConvType == Zq.ConvChannel)
        {
            var scene = JevCatalog.Instance.Scene(Store.GetTopicScene(message.AccountId, message.StreamId, message.Topic) ?? "") ?? defaultScene;
            var sender = message.IsOutgoing ? null : ParseIdentity(Store.GetIdentity(message.AccountId, message.SenderId));
            return new SceneChoice(scene, sender?.Scene == scene ? sender.Relationship : null);
        }
        // 一对一私聊为对方；群聊为发送者（自己发送的消息没有对方）
        var other = message.DmUserIds.Length == 1 ? message.DmUserIds[0] : message.IsOutgoing ? 0 : message.SenderId;
        return (other > 0 ? ParseIdentity(Store.GetIdentity(message.AccountId, other)) : null)
               ?? ParseIdentity(JsonUtil.Str(settings, DefaultIdentityKey))
               ?? new SceneChoice(defaultScene, null);
    }

    public static JevScene DefaultScene(JsonObject settings) =>
        JevCatalog.Instance.Scene(JsonUtil.Str(settings, DefaultSceneKey)) ?? JevCatalog.Instance.Scenes[0];

    /// <summary>解析“场景 id/关系 id”；不存在时为 null。</summary>
    public static SceneChoice? ParseIdentity(string? identity)
    {
        var parts = (identity ?? "").Split('/');
        if (parts.Length != 2 || JevCatalog.Instance.Scene(parts[0]) is not { } scene
            || scene.Relationships.FirstOrDefault(r => r.Id == parts[1]) is not { } relationship)
            return null;
        return new SceneChoice(scene, relationship);
    }

    /// <summary>话题面板的场景选项：第一项为使用默认场景（值为空）。</summary>
    public static JsonArray SceneOptions(JsonObject settings) =>
    [
        Option("", $"默认（{DefaultScene(settings).Label}）"),
        .. JevCatalog.Instance.Scenes.Select(s => Option(s.Id, s.Label)),
    ];

    /// <summary>用户面板的身份选项：第一项为不指定（值为空）。</summary>
    public static JsonArray IdentityOptions() =>
    [
        Option("", "不指定"),
        .. JevCatalog.Instance.Scenes.SelectMany(s => s.Relationships.Select(r => Option($"{s.Id}/{r.Id}", $"{s.Label} / {r.Label}"))),
    ];

    private static JsonNode Option(string value, string label) => new JsonObject { ["value"] = value, ["label"] = label };
}
