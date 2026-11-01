using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ZulipQQ.Plugins;

/// <summary>
/// 附加内容中的一个按钮：<see cref="Fill"/> 不为空时点击后把它填入输入框（不发送），
/// 否则打开 <see cref="Target"/>（URI 或程序），程序可以带参数。
/// </summary>
public sealed record PluginAction(string Label, string Target, string Args, string Fill = "")
{
    public bool IsFill => Fill.Length > 0;
}

/// <summary>
/// 插件附加在消息上的内容（set_annotation）。插件传入的 JSON：
/// <c>{"text": "显示在下方的文字", "replace": "临时替换气泡内容的文字", "actions": [{"label": "按钮文字",
/// "target": "https://… 或 程序路径", "args": "程序参数"} 或 {"label": "按钮文字", "fill": "填入输入框的文字"}]}</c>。
/// </summary>
public sealed record PluginAnnotation(string PluginId, string Text, IReadOnlyList<PluginAction> Actions, string Replace = "")
{
    public const int MaxTextLength = 4000;
    public const int MaxReplaceLength = 20000;
    public const int MaxActions = 5;
    public const int MaxLabelLength = 40;

    /// <summary>解析插件传入的 JSON；格式错误或超出限制时返回 null 并给出原因。text 与 actions 都为空时也返回 null（error 为 null）。</summary>
    public static PluginAnnotation? Parse(string pluginId, string json, out string? error)
    {
        error = null;
        JsonObject? root;
        try
        {
            root = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException ex)
        {
            error = $"JSON 格式错误：{ex.Message}";
            return null;
        }
        if (root is null)
        {
            error = "必须是 JSON 对象";
            return null;
        }
        var text = Str(root["text"]).Trim();
        if (text.Length > MaxTextLength)
        {
            error = $"text 超过 {MaxTextLength} 个字符";
            return null;
        }
        var replace = Str(root["replace"]).Trim();
        if (replace.Length > MaxReplaceLength)
        {
            error = $"replace 超过 {MaxReplaceLength} 个字符";
            return null;
        }
        var actions = new List<PluginAction>();
        if (root["actions"] is JsonArray array)
        {
            foreach (var a in array.OfType<JsonObject>())
            {
                var fill = Str(a["fill"]).Trim();
                var target = Str(a["target"]).Trim();
                var label = Str(a["label"]).Trim();
                if (fill.Length > MaxTextLength)
                {
                    error = $"fill 超过 {MaxTextLength} 个字符";
                    return null;
                }
                if (fill.Length == 0 && target.Length == 0)
                    continue;
                if (label.Length == 0)
                    label = fill.Length > 0 ? "填入输入框" : "打开";
                if (label.Length > MaxLabelLength)
                    label = label[..MaxLabelLength];
                actions.Add(fill.Length > 0 ? new PluginAction(label, "", "", fill) : new PluginAction(label, target, Str(a["args"]).Trim()));
            }
        }
        if (actions.Count > MaxActions)
        {
            error = $"按钮超过 {MaxActions} 个";
            return null;
        }
        return text.Length == 0 && actions.Count == 0 && replace.Length == 0 ? null : new PluginAnnotation(pluginId, text, actions, replace);
    }

    /// <summary>保存到数据库的 JSON（与插件传入的格式相同）。</summary>
    public string ToJson() => new JsonObject
    {
        ["text"] = Text,
        ["replace"] = Replace,
        ["actions"] = new JsonArray([.. Actions.Select(a => (JsonNode)(a.IsFill
            ? new JsonObject { ["label"] = a.Label, ["fill"] = a.Fill }
            : new JsonObject { ["label"] = a.Label, ["target"] = a.Target, ["args"] = a.Args }))]),
    }.ToJsonString();

    /// <summary>有需要显示在气泡下方的内容（只替换气泡内容时没有）。</summary>
    public bool HasCard => Text.Length > 0 || Actions.Count > 0;

    private static string Str(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>
/// 插件附加内容（单例，只在 UI 线程上使用）：只在本次运行中有效，宿主不保存。
/// 需要长期保留的结果由插件自己保存，在 zq_on_messages_shown 时重新附加。折叠状态与“显示原文”也只在本次运行中有效。
/// </summary>
public sealed class PluginAnnotationStore
{
    private readonly Dictionary<long, Dictionary<long, List<PluginAnnotation>>> _byAccount = [];
    private readonly HashSet<(long AccountId, long MessageId, string PluginId)> _collapsed = [];
    private readonly HashSet<(long AccountId, long MessageId)> _originalShown = [];

    private PluginAnnotationStore()
    {
    }

    public static PluginAnnotationStore Instance { get; } = new();

    /// <summary>某条消息的附加内容变化或折叠 / 展开：(账号 id, 消息 id)。</summary>
    public event EventHandler<(long AccountId, long MessageId)>? Changed;

    /// <summary>该消息的附加内容（按设置时间先后）。</summary>
    public IReadOnlyList<PluginAnnotation> Get(long accountId, long messageId) =>
        Account(accountId).TryGetValue(messageId, out var list) ? list : [];

    /// <summary>设置或清除（<paramref name="annotation"/> 为 null）某插件在该消息上的附加内容。</summary>
    public void Set(long accountId, long messageId, string pluginId, PluginAnnotation? annotation)
    {
        var messages = Account(accountId);
        if (!messages.TryGetValue(messageId, out var list))
            messages[messageId] = list = [];
        list.RemoveAll(a => a.PluginId == pluginId);
        if (annotation is not null)
        {
            // 新的替换内容（例如重新翻译）直接显示
            if (annotation.Replace.Length > 0)
                _originalShown.Remove((accountId, messageId));
            list.Add(annotation);
        }
        if (list.Count == 0)
            messages.Remove(messageId);
        Changed?.Invoke(this, (accountId, messageId));
    }

    public bool IsCollapsed(long accountId, long messageId, string pluginId) => _collapsed.Contains((accountId, messageId, pluginId));

    public void SetCollapsed(long accountId, long messageId, string pluginId, bool collapsed)
    {
        if (collapsed ? _collapsed.Add((accountId, messageId, pluginId)) : _collapsed.Remove((accountId, messageId, pluginId)))
            Changed?.Invoke(this, (accountId, messageId));
    }

    /// <summary>插件替换了气泡内容时，用户选择了显示原文。</summary>
    public bool IsOriginalShown(long accountId, long messageId) => _originalShown.Contains((accountId, messageId));

    public void SetOriginalShown(long accountId, long messageId, bool shown)
    {
        if (shown ? _originalShown.Add((accountId, messageId)) : _originalShown.Remove((accountId, messageId)))
            Changed?.Invoke(this, (accountId, messageId));
    }

    private Dictionary<long, List<PluginAnnotation>> Account(long accountId)
    {
        if (!_byAccount.TryGetValue(accountId, out var messages))
            _byAccount[accountId] = messages = [];
        return messages;
    }
}
