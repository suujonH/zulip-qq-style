using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace ZulipQQ.Plugins;

/// <summary>插件设置项的类型。</summary>
public enum PluginSettingType
{
    Text,
    Password,
    Multiline,
    Select,
    Bool,

    /// <summary>可增删的列表，每一行由 <see cref="PluginSettingField.Fields"/> 组成（不能嵌套列表）。</summary>
    List,

    /// <summary>
    /// 按钮（没有值，不保存）：点击时宿主以表单当前的值调用 zq_on_settings_button，
    /// 插件用 set_settings_feedback 把结果显示在按钮下方（例如“测试”）。
    /// </summary>
    Button,

    /// <summary>
    /// 字符串列表（例如模型名称）：可手动输入，也可从插件通过 set_settings_feedback 提供的候选中添加；
    /// 写了 <see cref="PluginSettingField.ItemButton"/> 时每一项旁边有一个按钮。
    /// </summary>
    Tags,

    /// <summary>选择其他插件发布的资源（<see cref="PluginSettingField.ResourceType"/>，例如模型中心的模型），值见 <see cref="PluginResourceSelection"/>。</summary>
    Resource,
}

/// <summary>下拉框的一个选项。</summary>
public sealed record PluginSettingOption(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// plugin.json 中 settings 的一项：宿主据此在“设置 &gt; 插件 &gt; 设置”中生成界面，值保存在宿主中，插件只能读取。
/// 值的 JSON 类型：Bool 为 true/false，List 为对象数组，Tags 为字符串数组，Resource 为对象（没有选择时为空对象），其余为字符串。
/// </summary>
public sealed partial record PluginSettingField
{
    public required string Key { get; init; }
    public required string Label { get; init; }
    public PluginSettingType Type { get; init; }
    public string Hint { get; init; } = "";
    public string Placeholder { get; init; } = "";

    /// <summary>默认值（已按类型校验）；没有写时为 null，取 <see cref="EmptyValue"/>。</summary>
    public JsonNode? Default { get; init; }

    public IReadOnlyList<PluginSettingOption> Options { get; init; } = [];

    /// <summary>列表每一行的设置项。</summary>
    public IReadOnlyList<PluginSettingField> Fields { get; init; } = [];

    /// <summary>Tags 每一项旁边的按钮文字（plugin.json 的 item_button）；空表示没有。点击时以该项调用 zq_on_settings_button。</summary>
    public string ItemButton { get; init; } = "";

    /// <summary>Resource 可以选择的资源类型（plugin.json 的 resource，例如 model），由发布资源的插件约定。</summary>
    public string ResourceType { get; init; } = "";

    /// <summary>Resource 发布资源的插件 id（plugin.json 的 plugin），需要在 dependencies 中声明。</summary>
    public string ResourcePlugin { get; init; } = "";

    /// <summary>
    /// 只在同一层（顶层或列表的同一行）的另一项取这些值之一时显示，例如 <c>"visible_when": { "type": ["topic"] }</c>；null 表示总是显示。
    /// </summary>
    public (string Key, IReadOnlyList<string> Values)? VisibleWhen { get; init; }

    [GeneratedRegex("^[a-z0-9_]{1,32}$")]
    private static partial Regex KeyPattern();

    /// <summary>该类型的空值。</summary>
    public JsonNode EmptyValue() => Type switch
    {
        PluginSettingType.Bool => JsonValue.Create(false),
        PluginSettingType.List or PluginSettingType.Tags => new JsonArray(),
        PluginSettingType.Resource => new JsonObject(),
        PluginSettingType.Select => JsonValue.Create(Options.Count > 0 ? Options[0].Value : ""),
        _ => JsonValue.Create(""),
    };

    /// <summary>默认值的副本。</summary>
    public JsonNode DefaultValue() => Default?.DeepClone() ?? EmptyValue();

    /// <summary>按类型取值：类型不对或没有时返回默认值。</summary>
    public JsonNode Normalize(JsonNode? value)
    {
        switch (Type)
        {
            case PluginSettingType.Bool:
                return value is JsonValue b && b.TryGetValue<bool>(out var flag) ? JsonValue.Create(flag) : DefaultValue();
            case PluginSettingType.List:
                if (value is not JsonArray array)
                    return DefaultValue();
                var rows = new JsonArray();
                foreach (var row in array.OfType<JsonObject>())
                {
                    var normalized = new JsonObject();
                    foreach (var f in Fields.Where(f => f.Type != PluginSettingType.Button))
                        normalized[f.Key] = f.Normalize(row[f.Key]);
                    rows.Add(normalized);
                }
                return rows;
            case PluginSettingType.Tags:
                if (value is not JsonArray tags)
                    return DefaultValue();
                return new JsonArray([.. tags.OfType<JsonValue>().Select(t => t.TryGetValue<string>(out var tag) ? tag.Trim() : "")
                    .Where(t => t.Length > 0).Distinct().Select(t => (JsonNode)JsonValue.Create(t))]);
            case PluginSettingType.Resource:
                return PluginResourceSelection.From(value as JsonObject) is { } selection && selection.Type == ResourceType
                       && selection.PluginId == ResourcePlugin
                    ? selection.ToJson()
                    : EmptyValue();
            case PluginSettingType.Select:
                return value is JsonValue v && v.TryGetValue<string>(out var s) && Options.Any(o => o.Value == s) ? JsonValue.Create(s) : DefaultValue();
            default:
                return value is JsonValue t && t.TryGetValue<string>(out var text) ? JsonValue.Create(text) : DefaultValue();
        }
    }

    /// <summary>解析 settings 数组；格式错误的项忽略并通过 <paramref name="warn"/> 报告。</summary>
    public static List<PluginSettingField> ParseList(JsonElement array, Action<string>? warn, bool allowList = true)
    {
        var result = new List<PluginSettingField>();
        if (array.ValueKind != JsonValueKind.Array)
            return result;
        foreach (var item in array.EnumerateArray())
        {
            try
            {
                var field = Parse(item, warn, allowList);
                if (result.Any(f => f.Key == field.Key))
                    throw new FormatException($"设置项 key 重复：{field.Key}");
                result.Add(field);
            }
            catch (FormatException ex)
            {
                warn?.Invoke($"忽略设置项：{ex.Message}");
            }
        }
        return result;
    }

    private static PluginSettingField Parse(JsonElement item, Action<string>? warn, bool allowList)
    {
        if (item.ValueKind != JsonValueKind.Object)
            throw new FormatException("必须是对象");
        var key = Str(item, "key");
        if (!KeyPattern().IsMatch(key))
            throw new FormatException($"key 不合法：{key}");
        var type = Str(item, "type") switch
        {
            "" or "text" => PluginSettingType.Text,
            "password" => PluginSettingType.Password,
            "multiline" => PluginSettingType.Multiline,
            "select" => PluginSettingType.Select,
            "bool" => PluginSettingType.Bool,
            "list" when allowList => PluginSettingType.List,
            "button" => PluginSettingType.Button,
            "tags" => PluginSettingType.Tags,
            "resource" => PluginSettingType.Resource,
            var t => throw new FormatException($"{key} 的类型不支持：{t}"),
        };

        var options = new List<PluginSettingOption>();
        if (type == PluginSettingType.Select)
        {
            if (item.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
            {
                foreach (var o in opts.EnumerateArray().Where(o => o.ValueKind == JsonValueKind.Object))
                {
                    var value = Str(o, "value");
                    if (value.Length > 0 && options.All(x => x.Value != value))
                        options.Add(new PluginSettingOption(value, Str(o, "label") is { Length: > 0 } l ? l : value));
                }
            }
            if (options.Count == 0)
                throw new FormatException($"{key} 没有选项");
        }

        var resourceType = Str(item, "resource");
        var resourcePlugin = Str(item, "plugin");
        if (type == PluginSettingType.Resource && !PluginResources.IsValidType(resourceType))
            throw new FormatException($"{key} 的资源类型不合法：{resourceType}");
        if (type == PluginSettingType.Resource && resourcePlugin.Length == 0)
            throw new FormatException($"{key} 没有填写发布资源的插件（plugin）");

        var fields = type == PluginSettingType.List && item.TryGetProperty("fields", out var fs)
            ? ParseList(fs, warn, allowList: false)
            : [];
        if (type == PluginSettingType.List && fields.Count == 0)
            throw new FormatException($"{key} 没有列表项");

        (string, IReadOnlyList<string>)? visibleWhen = null;
        if (item.TryGetProperty("visible_when", out var vw) && vw.ValueKind == JsonValueKind.Object)
        {
            var condition = vw.EnumerateObject().FirstOrDefault();
            if (condition.Value.ValueKind == JsonValueKind.String)
                visibleWhen = (condition.Name, [condition.Value.GetString()!]);
            else if (condition.Value.ValueKind == JsonValueKind.Array)
                visibleWhen = (condition.Name, condition.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!).ToList());
        }

        var field = new PluginSettingField
        {
            Key = key,
            Label = Str(item, "label") is { Length: > 0 } label ? label : key,
            Type = type,
            Hint = Str(item, "hint"),
            Placeholder = Str(item, "placeholder"),
            Options = options,
            Fields = fields,
            ItemButton = type == PluginSettingType.Tags ? Str(item, "item_button") : "",
            ResourceType = type == PluginSettingType.Resource ? resourceType : "",
            ResourcePlugin = type == PluginSettingType.Resource ? resourcePlugin : "",
            VisibleWhen = visibleWhen,
        };
        // 默认值类型不对时忽略
        return item.TryGetProperty("default", out var def) && JsonNode.Parse(def.GetRawText()) is { } node
            ? field with { Default = field.Normalize(node) }
            : field;
    }

    private static string Str(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
