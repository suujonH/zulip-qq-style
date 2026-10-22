using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZulipQQ.Plugins;

/// <summary>插件菜单项（设置 &gt; 插件中显示为按钮）。</summary>
public sealed record PluginMenu(int Id, string Name);

/// <summary>
/// 插件目录中的 plugin.json。<see cref="Parse"/> 校验失败时抛出 <see cref="FormatException"/>（消息直接显示给用户）。
/// </summary>
public sealed partial record PluginManifest
{
    /// <summary>宿主认识的权限。</summary>
    public static readonly IReadOnlySet<string> KnownPermissions =
        new HashSet<string> { "read_message", "send_message", "read_contacts", "modify_message", "upload_file" };

    /// <summary>可声明的事件，以及接收它需要的权限。</summary>
    public static readonly IReadOnlyDictionary<string, string> EventPermissions = new Dictionary<string, string>
    {
        ["on_message"] = "read_message",
        ["filter_incoming"] = "read_message",
        ["filter_outgoing"] = "modify_message",
    };

    /// <summary>只监控、不能拦截的优先级。</summary>
    public const int MonitorPriority = 10000;

    public const int MaxPriority = 40000;

    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "";
    public string Author { get; init; } = "";
    public string Description { get; init; } = "";
    public int ApiVersion { get; init; }
    public required string Library { get; init; }
    public IReadOnlySet<string> Permissions { get; init; } = new HashSet<string>();

    /// <summary>声明的事件及其优先级（数字越小越先执行）。</summary>
    public IReadOnlyDictionary<string, int> Events { get; init; } = new Dictionary<string, int>();

    public IReadOnlyList<PluginMenu> Menus { get; init; } = [];

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{2,63}$")]
    private static partial Regex IdPattern();

    /// <summary>
    /// 解析并校验。<paramref name="directoryName"/> 为插件所在目录名，必须与 id 相同；
    /// 不认识的权限和事件忽略，并通过 <paramref name="warn"/> 报告。
    /// </summary>
    public static PluginManifest Parse(string json, string directoryName, int hostApiVersion, Action<string>? warn = null)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            root = doc.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new FormatException($"plugin.json 格式错误：{ex.Message}", ex);
        }
        if (root.ValueKind != JsonValueKind.Object)
            throw new FormatException("plugin.json 必须是一个对象");

        var id = RequiredString(root, "id");
        if (!IdPattern().IsMatch(id))
            throw new FormatException($"id 不合法：{id}（只能包含小写字母、数字、点、下划线、减号，3～64 个字符）");
        if (!string.Equals(id, directoryName, StringComparison.Ordinal))
            throw new FormatException($"id（{id}）与插件目录名（{directoryName}）不一致");

        var apiVersion = root.TryGetProperty("api_version", out var api) && api.ValueKind == JsonValueKind.Number && api.TryGetInt32(out var v)
            ? v
            : throw new FormatException("缺少 api_version");
        if (apiVersion < 1 || apiVersion > hostApiVersion)
            throw new FormatException($"不支持的 api_version：{apiVersion}（宿主版本 {hostApiVersion}）");

        var library = RequiredString(root, "library");
        if (library.IndexOfAny(['/', '\\', ':']) >= 0 || library is "." or ".." || Path.GetFileName(library) != library)
            throw new FormatException($"library 只能是文件名：{library}");

        var permissions = new HashSet<string>();
        if (root.TryGetProperty("permissions", out var perms) && perms.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in perms.EnumerateArray().Where(p => p.ValueKind == JsonValueKind.String).Select(p => p.GetString()!))
            {
                if (KnownPermissions.Contains(p))
                    permissions.Add(p);
                else
                    warn?.Invoke($"忽略未知权限：{p}");
            }
        }

        var events = new Dictionary<string, int>();
        if (root.TryGetProperty("events", out var evs) && evs.ValueKind == JsonValueKind.Object)
        {
            foreach (var e in evs.EnumerateObject())
            {
                if (!EventPermissions.ContainsKey(e.Name))
                {
                    warn?.Invoke($"忽略未知事件：{e.Name}");
                    continue;
                }
                if (e.Value.ValueKind != JsonValueKind.Number || !e.Value.TryGetInt32(out var priority)
                    || priority < MonitorPriority || priority > MaxPriority)
                    throw new FormatException($"事件 {e.Name} 的优先级必须是 {MonitorPriority}～{MaxPriority} 的整数");
                events[e.Name] = priority;
            }
        }

        var menus = new List<PluginMenu>();
        if (root.TryGetProperty("menus", out var ms) && ms.ValueKind == JsonValueKind.Array)
        {
            foreach (var m in ms.EnumerateArray())
            {
                if (m.ValueKind == JsonValueKind.Object
                    && m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.TryGetInt32(out var menuId)
                    && m.TryGetProperty("name", out var mname) && mname.ValueKind == JsonValueKind.String
                    && mname.GetString() is { Length: > 0 } menuName)
                    menus.Add(new PluginMenu(menuId, menuName));
                else
                    warn?.Invoke("忽略格式错误的菜单项");
            }
        }

        return new PluginManifest
        {
            Id = id,
            Name = RequiredString(root, "name"),
            Version = OptionalString(root, "version"),
            Author = OptionalString(root, "author"),
            Description = OptionalString(root, "description"),
            ApiVersion = apiVersion,
            Library = library,
            Permissions = permissions,
            Events = events,
            Menus = menus,
        };
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : throw new FormatException($"缺少 {name}");

    private static string OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
