using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using PluginShared;

namespace ReplyAssistant;

/// <summary>jev 的一个场景（工作、恋爱、朋友、家人、通用）。</summary>
internal sealed record JevScene(string Id, string Label, string UnspecifiedNote, IReadOnlyList<JevRelationship> Relationships,
    IReadOnlyList<IReadOnlyList<string>> FrictionExamples, IReadOnlyList<JevQuestion> Questions);

/// <summary>场景中的一种关系；<see cref="Note"/> 为交给模型的英文说明。</summary>
internal sealed record JevRelationship(string Id, string Label, string Note);

/// <summary>
/// 一道是非题（kind 为 behavior 或 tone，role 为 action / passive / cue）；
/// <see cref="Relationships"/> 不为 null 时只在这些关系下提问。
/// </summary>
internal sealed record JevQuestion(string Id, string Kind, string Label, string Role, string Question, string Yes, string No,
    IReadOnlyList<string>? Relationships);

/// <summary>冲突程度的一个等级（0～5）。</summary>
internal sealed record JevLevel(string Label, string What);

/// <summary>
/// 从 jev（https://github.com/jev-chat/jev-chat-jarvis，MIT 许可）移植的分析题库：内嵌资源 jev-catalog.json。
/// 英文的题目与判断标准与 jev 发送的完全一致，中文标签只用于显示。
/// </summary>
internal sealed class JevCatalog
{
    private JevCatalog(string dataNote, string frictionOther, string frictionSelf, IReadOnlyList<JevLevel> levels, IReadOnlyList<JevScene> scenes)
    {
        DataNote = dataNote;
        FrictionOtherQuestion = frictionOther;
        FrictionSelfQuestion = frictionSelf;
        FrictionLevels = levels;
        Scenes = scenes;
    }

    public string DataNote { get; }
    public string FrictionOtherQuestion { get; }
    public string FrictionSelfQuestion { get; }
    public IReadOnlyList<JevLevel> FrictionLevels { get; }

    /// <summary>按 jev 的顺序：通用、工作、恋爱、朋友、家人（右键菜单的 id 依赖这个顺序）。</summary>
    public IReadOnlyList<JevScene> Scenes { get; }

    public static JevCatalog Instance { get; } = Load();

    public JevScene? Scene(string id) => Scenes.FirstOrDefault(s => s.Id == id);

    private static JevCatalog Load()
    {
        using var stream = typeof(JevCatalog).Assembly.GetManifestResourceStream("jev-catalog.json")
                           ?? throw new InvalidOperationException("缺少内嵌资源 jev-catalog.json");
        var root = (JsonObject)JsonNode.Parse(new StreamReader(stream).ReadToEnd())!;
        var friction = (JsonObject)root["friction"]!;
        var levels = ((JsonArray)friction["levels"]!).OfType<JsonObject>()
            .Select(l => new JevLevel(JsonUtil.Str(l, "label"), JsonUtil.Str(l, "what"))).ToList();
        var scenes = ((JsonArray)root["scenes"]!).OfType<JsonObject>().Select(s => new JevScene(
            JsonUtil.Str(s, "id"), JsonUtil.Str(s, "label"), JsonUtil.Str(s, "unspecified_note"),
            ((JsonArray)s["relationships"]!).OfType<JsonObject>()
                .Select(r => new JevRelationship(JsonUtil.Str(r, "id"), JsonUtil.Str(r, "label"), JsonUtil.Str(r, "note"))).ToList(),
            ((JsonArray)s["friction_examples"]!).Select(e => (IReadOnlyList<string>)Strings(e)).ToList(),
            ((JsonArray)s["questions"]!).OfType<JsonObject>().Select(q => new JevQuestion(
                JsonUtil.Str(q, "id"), JsonUtil.Str(q, "kind"), JsonUtil.Str(q, "label"), JsonUtil.Str(q, "role"),
                JsonUtil.Str(q, "question"), JsonUtil.Str(q, "yes"), JsonUtil.Str(q, "no"),
                q["relationships"] is JsonArray rs ? Strings(rs) : null)).ToList())).ToList();
        return new JevCatalog(JsonUtil.Str(root, "data_note"), JsonUtil.Str(friction, "other_question"),
            JsonUtil.Str(friction, "self_question"), levels, scenes);
    }

    private static List<string> Strings(JsonNode? node) =>
        (node as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : "").ToList();
}
