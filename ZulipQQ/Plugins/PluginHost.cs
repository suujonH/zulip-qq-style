using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.Plugins;

/// <summary>
/// 插件宿主（单例）。插件随程序启动加载（登录之前），会话开始 / 结束时通知插件，
/// 新消息、收到的消息过滤、发送前过滤按优先级依次调用插件（事件链）。
/// 公开方法只在 UI 线程上调用；UI 线程永远不同步等待插件线程，只做带超时的异步等待。
/// </summary>
public sealed class PluginHost
{
    private static readonly TimeSpan ChainTimeout = TimeSpan.FromSeconds(2);

    // 只在插件线程上、filter_outgoing 调用期间有效
    [ThreadStatic] private static bool _inOutgoingFilter;
    [ThreadStatic] private static string? _outgoingOverride;

    private readonly PluginThread _thread = new();
    private readonly List<NativePlugin> _plugins = [];
    private PluginStateRepository? _states;
    // UI 线程修改，插件线程读取
    private volatile bool _sessionActive;

    // 已经超时、仍在插件线程上执行的事件链：期间新的事件直接按 IGNORE 处理，避免越积越多
    private Task? _stuckCall;

    // 插件线程上正在调用的插件（超时时用于定位）
    private volatile NativePlugin? _current;

    private PluginHost()
    {
    }

    public static PluginHost Instance { get; } = new();

    /// <summary>插件安装目录：程序目录下的 plugins。</summary>
    public static string PluginsDirectory => Path.Combine(AppContext.BaseDirectory, "plugins");

    public IReadOnlyList<NativePlugin> Plugins => _plugins;

    /// <summary>插件的加载、启用状态变化（UI 线程）。</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// 扫描插件目录并加载已启用的插件（没有记录的插件默认启用）。只投递到插件线程，不等待。
    /// </summary>
    public void LoadAll(PluginStateRepository states)
    {
        _states = states;
        if (!Directory.Exists(PluginsDirectory))
            return;
        var enabled = states.LoadAll();
        foreach (var dir in Directory.GetDirectories(PluginsDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var plugin = ReadPlugin(dir);
            _plugins.Add(plugin);
            if (plugin.Manifest is not null && enabled.GetValueOrDefault(plugin.Id, true))
            {
                plugin.Enabled = true;
                _ = LoadAsync(plugin);
            }
        }
    }

    /// <summary>会话开始（登录完成）：依次调用已启用插件的 zq_on_start、zq_on_enable。</summary>
    public void OnSessionStarted()
    {
        _sessionActive = true;
        _ = _thread.RunAsync(() =>
        {
            foreach (var p in _plugins)
                Activate(p);
        });
    }

    /// <summary>
    /// 会话结束（退出账号或退出程序，在销毁会话之前）：对收到过 zq_on_enable / zq_on_start 的插件
    /// 依次调用 zq_on_disable、zq_on_exit。幂等；返回的任务在调用结束后完成，等待时间由调用方控制。
    /// </summary>
    public Task OnSessionEnded()
    {
        if (!_sessionActive)
            return Task.CompletedTask;
        _sessionActive = false;
        return _thread.RunAsync(() =>
        {
            foreach (var p in _plugins)
            {
                Deactivate(p);
                if (p.Started)
                {
                    p.Started = false;
                    Invoke(p, p.CallExit);
                }
            }
        });
    }

    /// <summary>启用或停用插件（保存状态）。停用只是不再派发事件，DLL 在重启后才会卸载。</summary>
    public async Task SetEnabledAsync(NativePlugin plugin, bool enabled)
    {
        if (plugin.Manifest is null || plugin.Enabled == enabled)
            return;
        _states?.SetEnabled(plugin.Id, enabled);
        plugin.Enabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        // 生命周期回调在插件线程上按当时的状态决定，期间又切换回来时不会重复或遗漏
        if (enabled && !plugin.IsLoaded)
            await LoadAsync(plugin);
        else
            await _thread.RunAsync(() => { if (plugin.Enabled) Activate(plugin); else Deactivate(plugin); });
    }

    /// <summary>插件菜单（不等待）。</summary>
    public void InvokeMenu(NativePlugin plugin, int menuId)
    {
        if (!plugin.Enabled || !plugin.IsLoaded || plugin.IsUnresponsive)
            return;
        _ = _thread.RunAsync(() => Invoke(plugin, () => plugin.CallMenu(menuId)));
    }

    /// <summary>
    /// 收到的消息过滤：返回 true 表示被插件拦截（不显示、不提醒，由调用方标记已读）。
    /// 没有插件处理该事件时同步返回 false。
    /// </summary>
    public async Task<bool> FilterIncomingAsync(ChatMessage message, ZulipSession session)
    {
        if (!AnyHandles("filter_incoming"))
            return false;
        var snapshot = PluginMessage.From(message, session);
        var result = await RunChainAsync("filter_incoming", p => p.CallFilterIncoming(snapshot));
        return result == ChainResult.Block;
    }

    /// <summary>新消息回调（不等待结果）。</summary>
    public void DispatchMessage(ChatMessage message, ZulipSession session)
    {
        if (!AnyHandles("on_message"))
            return;
        var snapshot = PluginMessage.From(message, session);
        _ = RunChainAsync("on_message", p => p.CallOnMessage(snapshot));
    }

    /// <summary>
    /// 发送前过滤：返回 null 表示被拦截，否则返回（可能被改写的）内容。超时时返回原内容。
    /// 每个插件看到的是前面的插件改写后的内容。
    /// </summary>
    public async Task<string?> FilterOutgoingAsync(ConversationKey key, string content, ZulipSession session)
    {
        if (!AnyHandles("filter_outgoing"))
            return content;
        var conv = PluginConversation.From(key, session);
        var current = content;
        var result = await RunChainAsync("filter_outgoing", p =>
        {
            var (r, changed) = CallOutgoing(p, conv, current);
            // 只监控的插件不能改写内容
            if (changed is not null && p.Priority("filter_outgoing") != PluginManifest.MonitorPriority)
                current = changed;
            return r;
        });
        return result switch
        {
            ChainResult.Block => null,
            ChainResult.TimedOut => content,
            _ => current,
        };
    }

    /// <summary>set_outgoing_content：只在插件线程上、filter_outgoing 调用期间有效。</summary>
    internal static bool TrySetOutgoingContent(string content)
    {
        if (!_inOutgoingFilter)
            return false;
        _outgoingOverride = content;
        return true;
    }

    /// <summary>程序退出：插件线程不再接受新的调用（不等待，进程退出时线程随之结束）。</summary>
    public void Shutdown() => _thread.Complete();

    private static NativePlugin ReadPlugin(string dir)
    {
        var name = Path.GetFileName(dir);
        try
        {
            var warnings = new List<string>();
            var manifest = PluginManifest.Parse(File.ReadAllText(Path.Combine(dir, "plugin.json")), name, Zq.ApiVersion, warnings.Add);
            var plugin = new NativePlugin(dir, manifest, null);
            foreach (var w in warnings)
                plugin.AddLog(Zq.LogWarning, "宿主", w);
            return plugin;
        }
        catch (FileNotFoundException)
        {
            return new NativePlugin(dir, null, "缺少 plugin.json");
        }
        catch (Exception ex) when (ex is FormatException or IOException or UnauthorizedAccessException)
        {
            Log.Warn($"插件清单无效：{name}", ex);
            return new NativePlugin(dir, null, ex.Message);
        }
    }

    private async Task LoadAsync(NativePlugin plugin)
    {
        try
        {
            await _thread.RunAsync(() =>
            {
                plugin.Load();
                Activate(plugin);
            });
        }
        catch (Exception ex)
        {
            Log.Warn($"加载插件失败：{plugin.Id}", ex);
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool AnyHandles(string eventName) => _plugins.Exists(p => p.Handles(eventName));

    private enum ChainResult
    {
        Ignore,
        Block,
        TimedOut,
    }

    /// <summary>
    /// 事件链：按优先级（相同时按 id）依次调用接收该事件的插件，返回 BLOCK 时停止；
    /// 优先级 10000 的插件只能监控，它返回 BLOCK 视为 IGNORE。UI 线程最多等待 2 秒，
    /// 超时后剩余的插件不再调用，正在执行的插件超时计数加 1。
    /// </summary>
    private async Task<ChainResult> RunChainAsync(string eventName, Func<NativePlugin, int> invoke)
    {
        var candidates = _plugins.Where(p => p.Handles(eventName))
            .OrderBy(p => p.Priority(eventName)).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0 || _stuckCall is not null)
            return ChainResult.Ignore;

        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var task = _thread.RunAsync(() =>
        {
            foreach (var p in candidates)
            {
                if (token.IsCancellationRequested)
                    return ChainResult.Ignore;
                if (!p.Enabled || p.IsUnresponsive)
                    continue;
                if (Invoke(p, () => invoke(p)) == Zq.EventBlock && p.Priority(eventName) != PluginManifest.MonitorPriority)
                    return ChainResult.Block;
            }
            return ChainResult.Ignore;
        });

        if (await Task.WhenAny(task, Task.Delay(ChainTimeout)) == task)
            return task.IsCompletedSuccessfully ? task.Result : ChainResult.Ignore;

        cancel.Cancel();
        var stuck = _current;
        Log.Warn($"插件事件 {eventName} 超时：{stuck?.Id ?? "未知"}");
        if (stuck is not null)
        {
            stuck.OnTimeout();
            stuck.AddLog(Zq.LogWarning, "宿主", $"{eventName} 超过 {ChainTimeout.TotalSeconds:0} 秒没有返回"
                + (stuck.IsUnresponsive ? "，连续多次超时，本次运行内不再派发事件" : ""));
        }
        _stuckCall = task;
        _ = task.ContinueWith(_ => Dispatcher.UIThread.Post(() =>
        {
            if (_stuckCall == task)
                _stuckCall = null;
        }), TaskScheduler.Default);
        Changed?.Invoke(this, EventArgs.Empty);
        return ChainResult.TimedOut;
    }

    // 插件线程上：会话进行中且插件已启用、已加载时，补发 zq_on_start、zq_on_enable
    private void Activate(NativePlugin plugin)
    {
        if (!_sessionActive || !plugin.Enabled || !plugin.IsLoaded)
            return;
        if (!plugin.Started)
        {
            plugin.Started = true;
            Invoke(plugin, plugin.CallStart);
        }
        if (!plugin.Active)
        {
            plugin.Active = true;
            Invoke(plugin, plugin.CallEnable);
        }
    }

    // 插件线程上：收到过 zq_on_enable 的插件调用 zq_on_disable
    private void Deactivate(NativePlugin plugin)
    {
        if (!plugin.Active)
            return;
        plugin.Active = false;
        Invoke(plugin, plugin.CallDisable);
    }

    // 插件线程上：调用一个导出函数，记录正在执行的插件；托管异常记入插件日志（原生崩溃无法捕获）。
    // 只有在超时时间内返回才清零超时计数：每次都超时但最终返回的插件仍会被标记为无响应
    private int Invoke(NativePlugin plugin, Func<int> call)
    {
        _current = plugin;
        var watch = Stopwatch.StartNew();
        try
        {
            var result = call();
            if (watch.Elapsed < ChainTimeout)
                plugin.OnReturned();
            return result;
        }
        catch (Exception ex)
        {
            plugin.AddLog(Zq.LogError, "宿主", $"调用插件失败：{ex.Message}");
            return Zq.EventIgnore;
        }
        finally
        {
            _current = null;
        }
    }

    // 插件线程上：调用 zq_filter_outgoing，返回结果与插件通过 set_outgoing_content 设置的新内容
    private static unsafe (int Result, string? Changed) CallOutgoing(NativePlugin plugin, PluginConversation conv, string content)
    {
        using var scope = new Utf8Scope();
        var native = new ZqOutgoing { StructSize = (uint)sizeof(ZqOutgoing), Conv = conv.ToNative(scope), Content = scope.Str(content) };
        _inOutgoingFilter = true;
        _outgoingOverride = null;
        try
        {
            return (plugin.FilterOutgoing(&native), _outgoingOverride);
        }
        finally
        {
            _inOutgoingFilter = false;
            _outgoingOverride = null;
        }
    }
}
