using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZulipQQ.Plugins;

/// <summary>
/// plugin.json 的 panels 中的一项：<c>{"kind": "user" | "topic", "label": "好友身份"}</c>。
/// 宿主在用户资料卡 / 话题的资料面板中为插件显示一个分组（标题为插件名），在插件设置中显示一个以 label 为标题的列表；
/// 其中的设置项和数据都由插件通过 zq_on_panel 提供并自行保存。
/// </summary>
public sealed record PluginPanel(PluginTargetKind Kind, string Label)
{
    public static string KindName(PluginTargetKind kind) => kind == PluginTargetKind.User ? "user" : "topic";

    public static PluginTargetKind? ParseKind(string? name) => name switch
    {
        "user" => PluginTargetKind.User,
        "topic" => PluginTargetKind.Topic,
        _ => null,
    };

    /// <summary>zq_on_panel 的参数：<c>{"action", "kind", "target"?, "key"?, "value"?}</c>。</summary>
    public static JsonObject Request(string action, PluginTargetKind kind, PluginTarget? target = null, string? key = null, JsonNode? value = null)
    {
        var request = new JsonObject { ["action"] = action, ["kind"] = KindName(kind) };
        if (target is not null)
            request["target"] = target.ToJson();
        if (key is not null)
        {
            request["key"] = key;
            request["value"] = value?.DeepClone();
        }
        return request;
    }
}

/// <summary>面板中下拉框的一个选项。</summary>
public sealed record PluginPanelOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// 面板中的一项（zq_on_panel 的 get / set 通过 reply_panel 回复）：
/// <c>{"key", "label", "type": "select", "options": [{"value", "label"}], "value": "选中的值"}</c>
/// 或 <c>{"key", "label", "type": "bool", "value": true}</c>。
/// </summary>
public sealed record PluginPanelField(string Key, string Label, bool IsBool, IReadOnlyList<PluginPanelOption> Options, string SelectedValue,
    bool IsChecked)
{
    public const int MaxFields = 20;
    public const int MaxOptions = 200;

    /// <summary>解析回复中的 fields；格式错误的项忽略（下拉框至少要有一个选项）。</summary>
    public static IReadOnlyList<PluginPanelField> Parse(JsonObject? reply)
    {
        var fields = new List<PluginPanelField>();
        foreach (var f in (reply?["fields"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxFields))
        {
            var key = Str(f["key"]);
            var label = Str(f["label"]);
            if (key.Length == 0 || label.Length == 0 || fields.Exists(x => x.Key == key))
                continue;
            switch (Str(f["type"]))
            {
                case "bool":
                    fields.Add(new PluginPanelField(key, label, true, [], "",
                        f["value"] is JsonValue b && b.TryGetValue<bool>(out var on) && on));
                    break;
                case "select":
                    var options = (f["options"] as JsonArray ?? []).OfType<JsonObject>().Take(MaxOptions)
                        .Select(o => new PluginPanelOption(Str(o["value"]), Str(o["label"])))
                        .Where(o => o.Label.Length > 0)
                        .DistinctBy(o => o.Value)
                        .ToList();
                    if (options.Count > 0)
                        fields.Add(new PluginPanelField(key, label, false, options, Str(f["value"]), false));
                    break;
            }
        }
        return fields;
    }

    internal static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>插件设置中面板列表的一行（zq_on_panel 的 list 回复 <c>{"entries": [{"target", "text"}]}</c>）。</summary>
public sealed record PluginPanelEntry(PluginTarget Target, string Text)
{
    public static IReadOnlyList<PluginPanelEntry> Parse(JsonObject? reply, PluginTargetKind kind) =>
        (reply?["entries"] as JsonArray ?? []).OfType<JsonObject>()
        .Select(e => PluginTarget.From(e["target"] as JsonObject, kind) is { } target
            ? new PluginPanelEntry(target, PluginPanelField.Str(e["text"]))
            : null)
        .OfType<PluginPanelEntry>()
        .ToList();
}

/// <summary>解析插件回复的 JSON 对象；不是对象时为 null。</summary>
internal static class PluginReply
{
    public static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return null;
        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
