using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件发布的一项资源（例如模型中心的一个 provider）：<see cref="Items"/> 为其中可选的项（例如模型名称）。
/// 其他插件在设置中选择 (插件, 资源, 项)，使用时由宿主把请求转交给发布资源的插件。
/// </summary>
public sealed record PluginResource(string PluginId, string Id, string Name, IReadOnlyList<string> Items);

/// <summary>
/// 插件间共享的资源（单例，线程安全）。宿主只是通道：资源类型（type）与请求、回复的格式由发布资源的插件自己约定，
/// 宿主不理解其内容。使用者需要在 plugin.json 的 dependencies 中声明发布者。
/// 资源只保存在内存中（插件每次加载、设置变化时重新发布）；发布者未开启时不可用。
/// </summary>
public sealed partial class PluginResources
{
    [GeneratedRegex("^[a-z0-9_]{1,64}$")]
    private static partial Regex TypePattern();

    /// <summary>资源类型只能由小写字母、数字和下划线组成（1～64 个字符）。</summary>
    public static bool IsValidType(string type) => TypePattern().IsMatch(type);

    /// <summary>等待发布资源的插件回复的时间。</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(120);

    private readonly Lock _lock = new();
    // (插件 id, 类型) → 资源
    private readonly Dictionary<(string PluginId, string Type), IReadOnlyList<PluginResource>> _published = [];
    // 请求 id → (发布资源的插件, 回复)
    private readonly ConcurrentDictionary<long, (NativePlugin Provider, TaskCompletionSource<string> Reply)> _pending = new();
    private long _nextRequestId;

    private PluginResources()
    {
    }

    public static PluginResources Instance { get; } = new();

    /// <summary>发布的资源变化（UI 线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 解析 publish_resources 的 JSON：<c>[{"id": "OpenAI", "name": "OpenAI", "items": ["gpt-4o"]}]</c>。
    /// id 为空、重复的资源忽略（先出现的有效），name 为空时取 id，items 去掉空项和重复项。格式错误时返回 null。
    /// </summary>
    public static List<PluginResource>? Parse(string pluginId, string json)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(json) as JsonArray;
        }
        catch (JsonException)
        {
            return null;
        }
        if (array is null)
            return null;
        var result = new List<PluginResource>();
        foreach (var obj in array.OfType<JsonObject>())
        {
            var id = Str(obj, "id").Trim();
            if (id.Length == 0 || result.Any(r => r.Id == id))
                continue;
            var items = (obj["items"] as JsonArray ?? []).OfType<JsonValue>()
                .Select(v => v.TryGetValue<string>(out var s) ? s.Trim() : "")
                .Where(s => s.Length > 0).Distinct().ToList();
            result.Add(new PluginResource(pluginId, id, Str(obj, "name").Trim() is { Length: > 0 } name ? name : id, items));
        }
        return result;
    }

    /// <summary>替换插件发布的某类资源（任何线程）；空列表表示清除。</summary>
    public void Publish(string pluginId, string type, IReadOnlyList<PluginResource> resources)
    {
        lock (_lock)
        {
            if (resources.Count == 0)
                _published.Remove((pluginId, type));
            else
                _published[(pluginId, type)] = resources;
        }
        Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    /// <summary>插件 <paramref name="pluginId"/> 发布的某类资源；发布者未加载、未开启或被标记为无响应时为空。</summary>
    public IReadOnlyList<PluginResource> Available(string pluginId, string type)
    {
        var providers = PluginHost.Instance.Plugins.Where(p => p.Id == pluginId && IsUsableProvider(p)).ToList();
        lock (_lock)
        {
            return providers.SelectMany(p => _published.GetValueOrDefault((p.Id, type)) ?? []).ToList();
        }
    }

    private static bool IsUsableProvider(NativePlugin plugin) =>
        plugin.IsLoaded && plugin.Enabled && !plugin.IsUnresponsive && plugin.HandlesResourceRequests;

    /// <summary>
    /// 把 <paramref name="consumer"/> 的请求转交给发布资源的插件并等待回复（阻塞，不能在插件线程或 UI 线程上调用）。
    /// 返回 (结果码, 回复的 JSON)：资源不存在或发布者不可用为 ERR_NOT_FOUND，超时为 ERR_NETWORK。
    /// </summary>
    internal (int Code, string? Reply) Request(NativePlugin consumer, PluginResourceSelection target, JsonNode? request)
    {
        var (type, pluginId, resourceId, item) = target;
        var provider = PluginHost.Instance.Plugins.FirstOrDefault(p => p.Id == pluginId);
        if (provider is null || !IsUsableProvider(provider))
            return (Zq.ErrNotFound, null);
        lock (_lock)
        {
            if (_published.GetValueOrDefault((pluginId, type))?.FirstOrDefault(r => r.Id == resourceId) is not { } resource
                || (item.Length > 0 && !resource.Items.Contains(item)))
                return (Zq.ErrNotFound, null);
        }

        var id = Interlocked.Increment(ref _nextRequestId);
        var reply = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (provider, reply);
        try
        {
            var payload = new JsonObject
            {
                ["type"] = type,
                ["resource"] = resourceId,
                ["item"] = item,
                ["request"] = request?.DeepClone(),
                ["from"] = consumer.Id,
            }.ToJsonString();
            PluginHost.Instance.InvokeResourceRequest(provider, id, payload);
            return reply.Task.Wait(RequestTimeout) ? (Zq.Ok, reply.Task.Result) : (Zq.ErrNetwork, null);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>发布资源的插件回复请求（任何线程）；请求不存在、已超时或不是发给该插件的返回 false。</summary>
    internal bool Reply(NativePlugin provider, long requestId, string json)
    {
        if (!_pending.TryGetValue(requestId, out var pending) || pending.Provider != provider)
            return false;
        return pending.Reply.TrySetResult(json);
    }

    private static string Str(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}

/// <summary>
/// 设置中选中的资源（resource 类型设置项的值）：<c>{"type": "model", "plugin": "插件 id", "resource": "资源 id", "item": "项"}</c>。
/// </summary>
public sealed record PluginResourceSelection(string Type, string PluginId, string ResourceId, string Item)
{
    /// <summary>类型不合法、插件或资源为空时返回 null。</summary>
    public static PluginResourceSelection? From(JsonObject? obj)
    {
        if (obj is null)
            return null;
        var selection = new PluginResourceSelection(Str(obj, "type"), Str(obj, "plugin"), Str(obj, "resource"), Str(obj, "item"));
        return PluginResources.IsValidType(selection.Type) && selection.PluginId.Length > 0 && selection.ResourceId.Length > 0
            ? selection
            : null;
    }

    public JsonObject ToJson() => new() { ["type"] = Type, ["plugin"] = PluginId, ["resource"] = ResourceId, ["item"] = Item };

    private static string Str(JsonObject obj, string name) => obj[name] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";
}
