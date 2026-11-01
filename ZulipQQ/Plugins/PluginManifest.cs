using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ZulipQQ.Plugins;

/// <summary>插件菜单项（设置 &gt; 插件中显示为按钮）。</summary>
/// <summary>插件菜单项；<see cref="Children"/> 不为空时是子菜单（只在 message_menus 中，点击子菜单本身不调用插件）。</summary>
public sealed record PluginMenu(int Id, string Name)
{
    public IReadOnlyList<PluginMenu> Children { get; init; } = Array.Empty<PluginMenu>();

    public bool IsGroup => Children.Count > 0;

    /// <summary>
    /// 可以绑定快捷操作（<c>"shortcut"</c>，见 <see cref="PluginShortcuts"/>）：null 表示不可绑定，空字符串表示可绑定、没有默认值，
    /// 否则为默认值（message_menus 为鼠标手势，menus 为键盘快捷键，均已规范化）。
    /// </summary>
    public string? Shortcut { get; init; }
}

/// <summary>
/// 插件目录中的 plugin.json。<see cref="Parse"/> 校验失败时抛出 <see cref="FormatException"/>（消息直接显示给用户）。
/// </summary>
public sealed partial record PluginManifest
{
    /// <summary>宿主认识的权限（见 <see cref="PermissionCatalog"/>）。</summary>
    public static readonly IReadOnlySet<string> KnownPermissions = PermissionCatalog.All.Select(p => p.Id).ToHashSet();

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

    /// <summary>消息右键菜单中的菜单项（点击时调用 zq_on_message_menu，需要 read_message）。</summary>
    public IReadOnlyList<PluginMenu> MessageMenus { get; init; } = [];

    /// <summary>launch 白名单：开启时告知用户，之后可以不经点击直接打开。</summary>
    public IReadOnlyList<LaunchRule> Launch { get; init; } = [];

    /// <summary>插件设置项（宿主生成界面）。</summary>
    public IReadOnlyList<PluginSettingField> Settings { get; init; } = [];

    /// <summary>依赖的插件 id（plugin.json 的 dependencies）：开启前需要先开启它们，可以使用它们发布的资源。</summary>
    public IReadOnlyList<string> Dependencies { get; init; } = [];

    /// <summary>用户资料卡 / 话题资料面板中的插件分组（plugin.json 的 panels，每种最多一个）。</summary>
    public IReadOnlyList<PluginPanel> Panels { get; init; } = [];

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

        var menus = ParseMenus(root, "menus", warn, depth: 0);
        var messageMenus = ParseMenus(root, "message_menus", warn, depth: MaxMenuDepth);
        if (messageMenus.Count > 0 && !permissions.Contains("read_message"))
            warn?.Invoke("声明了 message_menus 但没有声明 read_message 权限，消息菜单不会显示");

        var launch = new List<LaunchRule>();
        if (root.TryGetProperty("launch", out var ls) && ls.ValueKind == JsonValueKind.Array)
        {
            foreach (var l in ls.EnumerateArray().Where(l => l.ValueKind == JsonValueKind.String).Select(l => l.GetString()!))
            {
                if (LaunchRule.TryParse(l, out var error) is { } rule)
                    launch.Add(rule);
                else
                    warn?.Invoke($"忽略白名单项：{error}");
            }
            if (launch.Count > 0 && !permissions.Contains("launch"))
                warn?.Invoke("声明了 launch 白名单但没有声明 launch 权限，白名单不会生效");
        }

        var dependencies = new List<string>();
        if (root.TryGetProperty("dependencies", out var deps) && deps.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in deps.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!))
            {
                if (!IdPattern().IsMatch(d) || d == id)
                    warn?.Invoke($"忽略依赖：{d}");
                else if (!dependencies.Contains(d))
                    dependencies.Add(d);
            }
        }

        var panels = new List<PluginPanel>();
        if (root.TryGetProperty("panels", out var ps) && ps.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in ps.EnumerateArray())
            {
                var kind = p.ValueKind == JsonValueKind.Object ? PluginPanel.ParseKind(OptionalString(p, "kind")) : null;
                var label = p.ValueKind == JsonValueKind.Object ? OptionalString(p, "label") : "";
                if (kind is null || label.Length == 0 || panels.Exists(x => x.Kind == kind))
                    warn?.Invoke("忽略格式错误或重复的面板");
                else
                    panels.Add(new PluginPanel(kind.Value, label));
            }
        }

        var settings = root.TryGetProperty("settings", out var ss) ? PluginSettingField.ParseList(ss, warn) : [];
        foreach (var plugin in settings.Where(f => f.Type == PluginSettingType.Resource).Select(f => f.ResourcePlugin).Distinct())
        {
            if (!dependencies.Contains(plugin))
                warn?.Invoke($"声明了 resource 设置项但 dependencies 中没有 {plugin}，无法使用选择的资源");
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
            MessageMenus = messageMenus,
            Launch = launch,
            Settings = settings,
            Dependencies = dependencies,
            Panels = panels,
        };
    }

    /// <summary>message_menus 子菜单最多嵌套的层数。</summary>
    private const int MaxMenuDepth = 2;

    // depth：还允许嵌套的层数。子菜单写作 {"name": "...", "children": [...]}（不需要 id），空的子菜单忽略
    private static List<PluginMenu> ParseMenus(JsonElement root, string name, Action<string>? warn, int depth) =>
        root.TryGetProperty(name, out var ms) ? ParseMenuList(ms, warn, depth, keyboard: name == "menus") : [];

    // keyboard：shortcut 为键盘快捷键（menus），否则为鼠标手势（message_menus）
    private static List<PluginMenu> ParseMenuList(JsonElement ms, Action<string>? warn, int depth, bool keyboard)
    {
        var menus = new List<PluginMenu>();
        if (ms.ValueKind != JsonValueKind.Array)
            return menus;
        foreach (var m in ms.EnumerateArray())
        {
            if (m.ValueKind != JsonValueKind.Object || !m.TryGetProperty("name", out var mname) || mname.ValueKind != JsonValueKind.String
                || mname.GetString() is not { Length: > 0 } menuName)
            {
                warn?.Invoke("忽略格式错误的菜单项");
                continue;
            }
            if (m.TryGetProperty("children", out var children))
            {
                if (depth <= 0)
                {
                    warn?.Invoke($"忽略子菜单（不支持或嵌套过深）：{menuName}");
                    continue;
                }
                var items = ParseMenuList(children, warn, depth - 1, keyboard);
                if (items.Count > 0)
                    menus.Add(new PluginMenu(0, menuName) { Children = items });
            }
            else if (m.TryGetProperty("id", out var mid) && mid.ValueKind == JsonValueKind.Number && mid.TryGetInt32(out var menuId))
                menus.Add(new PluginMenu(menuId, menuName) { Shortcut = ParseShortcut(m, menuName, warn, keyboard) });
            else
                warn?.Invoke("忽略格式错误的菜单项");
        }
        return menus;
    }

    // "shortcut": "" 或默认值；格式错误或不能绑定的默认值当作没有默认值
    private static string? ParseShortcut(JsonElement m, string menuName, Action<string>? warn, bool keyboard)
    {
        if (!m.TryGetProperty("shortcut", out var value))
            return null;
        var gesture = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
        if (gesture.Length == 0)
            return "";
        var normalized = keyboard
            ? PluginShortcuts.NormalizeKey(gesture)
            : PluginShortcuts.IsAllowedMouse(gesture) ? PluginShortcuts.NormalizeMouse(gesture) : null;
        if (normalized is not null)
            return normalized;
        warn?.Invoke($"忽略菜单 {menuName} 不能使用的快捷操作：{gesture}");
        return "";
    }

    private static string RequiredString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } s
            ? s
            : throw new FormatException($"缺少 {name}");

    private static string OptionalString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
