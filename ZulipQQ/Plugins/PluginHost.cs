using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
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

    // 只在插件线程上、zq_on_panel 调用期间有效
    [ThreadStatic] private static bool _inPanel;
    [ThreadStatic] private static string? _panelReply;

    // 插件线程上：当前回调所属的会话（宿主函数只用它，避免旧会话的回调使用新登录的账号）
    [ThreadStatic] private static ZulipSession? _callSession;

    private readonly PluginThread _thread = new();
    private readonly List<NativePlugin> _plugins = [];
    private PluginStateRepository? _states;
    private SettingRepository? _appSettings;
    private Dictionary<string, string> _shortcuts = [];
    // 已登录的会话（按登录顺序，第一个是插件的默认账号）：UI 线程整体替换，插件线程和插件自己的线程读取
    private volatile ZulipSession[] _sessions = [];

    // 已经超时、仍在插件线程上执行的事件链：期间新的事件直接按 IGNORE 处理，避免越积越多
    private Task? _stuckCall;

    // 插件线程上正在调用的插件与调用序号（超时时用于定位；同一次调用只计一次超时）
    private volatile NativePlugin? _current;
    private long _currentCall;
    private long _chargedCall;

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
    /// 扫描插件目录并加载已启用、且文件与用户确认开启时一致（指纹相同）的插件；新插件默认停用。
    /// 只投递到插件线程，不等待。
    /// </summary>
    public void LoadAll(PluginStateRepository states, SettingRepository appSettings)
    {
        _states = states;
        _appSettings = appSettings;
        _shortcuts = PluginShortcuts.Load(appSettings);
        if (!Directory.Exists(PluginsDirectory))
            return;
        var enabled = states.LoadAll();
        var approvals = states.LoadApprovals();
        foreach (var dir in Directory.GetDirectories(PluginsDirectory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var plugin = ReadPlugin(dir);
            _plugins.Add(plugin);
            if (plugin.Manifest is not { } manifest)
                continue;
            plugin.Fingerprint = PluginFingerprint.Compute(dir);
            plugin.ApprovedFingerprint = approvals.GetValueOrDefault(plugin.Id);
            plugin.SettingsJson = PluginSettings.Load(appSettings, manifest).ToJsonString();
            if (plugin.NeedsReapproval)
                plugin.AddLog(Zq.LogWarning, "宿主", "插件文件与上次确认开启时不同，需要重新开启");
            if (plugin.IsApproved && enabled.GetValueOrDefault(plugin.Id))
            {
                plugin.Enabled = true;
                _ = LoadAsync(plugin);
            }
        }
    }

    /// <summary>已登录的会话（插件线程上也可以读取）。</summary>
    internal IReadOnlyList<ZulipSession> Sessions => _sessions;

    /// <summary>
    /// 回调之外调用宿主函数时使用的会话：插件用 select_account 选择的账号，没有选择或已退出时为最先登录的账号。
    /// </summary>
    internal ZulipSession? DefaultSession(NativePlugin? plugin)
    {
        var sessions = _sessions;
        var selected = plugin?.SelectedAccountId ?? 0;
        return (selected != 0 ? Array.Find(sessions, s => s.Account.Id == selected) : null) ?? sessions.FirstOrDefault();
    }

    /// <summary>
    /// 一个账号登录完成：第一个账号登录时依次调用已启用插件的 zq_on_start、zq_on_enable；
    /// 每个账号登录时调用 zq_on_account_login。
    /// </summary>
    public void OnSessionStarted(ZulipSession session)
    {
        if (_sessions.Contains(session))
            return;
        _sessions = [.. _sessions, session];
        var accountId = session.Account.Id;
        _ = RunAsync(session, () =>
        {
            foreach (var p in _plugins)
            {
                Activate(p);
                if (p.Active)
                    Invoke(p, () => p.CallAccountLogin(accountId));
            }
            return true;
        });
    }

    /// <summary>
    /// 一个账号结束（退出该账号或退出程序，在销毁会话之前）：调用 zq_on_account_logout；
    /// 最后一个账号结束时，对收到过 zq_on_enable / zq_on_start 的插件依次调用 zq_on_disable、zq_on_exit。
    /// 幂等；返回的任务在调用结束后完成，等待时间由调用方控制。
    /// </summary>
    public Task OnSessionEnded(ZulipSession session)
    {
        if (!_sessions.Contains(session))
            return Task.CompletedTask;
        _sessions = [.. _sessions.Where(s => s != session)];
        var last = _sessions.Length == 0;
        var accountId = session.Account.Id;
        return RunAsync(session, () =>
        {
            foreach (var p in _plugins)
            {
                if (p.Active)
                    Invoke(p, () => p.CallAccountLogout(accountId));
                if (!last)
                    continue;
                Deactivate(p);
                if (p.Started)
                {
                    p.Started = false;
                    Invoke(p, p.CallExit);
                }
            }
            return true;
        });
    }

    /// <summary>
    /// 用户在开启确认中同意：重新计算指纹，与扫描时（确认框显示的清单）一致才记录并启用。
    /// 返回 false 表示插件文件在此期间发生了变化（需要重启程序重新读取）。
    /// </summary>
    public async Task<bool> ApproveAsync(NativePlugin plugin)
    {
        if (plugin.Manifest is null || plugin.Fingerprint is null || PluginFingerprint.Compute(plugin.DirectoryPath) != plugin.Fingerprint)
            return false;
        _states?.Approve(plugin.Id, plugin.Fingerprint);
        plugin.ApprovedFingerprint = plugin.Fingerprint;
        await SetEnabledAsync(plugin, true);
        return true;
    }

    /// <summary>
    /// 启用或停用插件（保存状态）。停用只是不再派发事件，DLL 在重启后才会卸载。
    /// 启用只对已确认的插件有效（未确认时先调用 <see cref="ApproveAsync"/>）。
    /// </summary>
    public async Task SetEnabledAsync(NativePlugin plugin, bool enabled)
    {
        if (plugin.Manifest is null || plugin.Enabled == enabled || (enabled && !plugin.IsApproved))
            return;
        _states?.SetEnabled(plugin.Id, enabled);
        plugin.Enabled = enabled;
        Changed?.Invoke(this, EventArgs.Empty);
        // 生命周期回调在插件线程上按当时的状态决定，期间又切换回来时不会重复或遗漏
        if (enabled && !plugin.IsLoaded)
            await LoadAsync(plugin);
        else
            await RunAsync(DefaultSession(plugin), () =>
            {
                if (plugin.Enabled)
                    Activate(plugin);
                else
                    Deactivate(plugin);
                return true;
            });
    }

    /// <summary>插件设置的当前值（解密后，含默认值）。</summary>
    public JsonObject LoadSettings(NativePlugin plugin) =>
        plugin.Manifest is { } m && _appSettings is not null ? PluginSettings.Load(_appSettings, m) : [];

    /// <summary>保存插件设置并通知插件（zq_on_settings_changed，不等待）。</summary>
    public void SaveSettings(NativePlugin plugin, JsonObject values)
    {
        if (plugin.Manifest is not { } m || _appSettings is null)
            return;
        PluginSettings.Save(_appSettings, m, values);
        plugin.SettingsJson = PluginSettings.Load(_appSettings, m).ToJsonString();
        if (plugin.Enabled && plugin.IsLoaded && !plugin.IsUnresponsive)
            _ = RunAsync(DefaultSession(plugin), () => Invoke(plugin, plugin.CallSettingsChanged));
    }

    /// <summary>消息右键菜单（不等待）：在 UI 线程上取消息快照，在插件线程上以消息所属的账号调用 zq_on_message_menu。</summary>
    public void InvokeMessageMenu(NativePlugin plugin, int menuId, ChatMessage message, ZulipSession session)
    {
        if (plugin.ActiveMessageMenus.Count == 0)
            return;
        var snapshot = PluginMessage.From(message, session, historical: true);
        _ = RunAsync(session, () => Invoke(plugin, () => plugin.CallMessageMenu(menuId, snapshot)));
    }

    /// <summary>
    /// 插件面板（用户资料卡、话题资料面板、插件设置中的列表）：以 target 所属的账号调用 zq_on_panel，返回插件通过 reply_panel 回复的对象。
    /// 插件未开启、没有回复、回复格式错误或 2 秒内没有返回时为 null（不计入超时次数）。
    /// </summary>
    public async Task<JsonObject?> InvokePanelAsync(NativePlugin plugin, JsonObject request, long accountId)
    {
        if (plugin.ActivePanels.Count == 0)
            return null;
        var json = request.ToJsonString();
        var session = Array.Find(_sessions, s => s.Account.Id == accountId) ?? DefaultSession(plugin);
        var task = RunAsync(session, () =>
        {
            _inPanel = true;
            _panelReply = null;
            try
            {
                Invoke(plugin, () => plugin.CallPanel(json));
                return _panelReply;
            }
            finally
            {
                _inPanel = false;
                _panelReply = null;
            }
        });
        if (await Task.WhenAny(task, Task.Delay(ChainTimeout)) != task || !task.IsCompletedSuccessfully)
            return null;
        return PluginReply.Parse(task.Result);
    }

    /// <summary>reply_panel：只在插件线程上、zq_on_panel 调用期间有效。</summary>
    internal static bool TryReplyPanel(string json)
    {
        if (!_inPanel)
            return false;
        _panelReply = json;
        return true;
    }

    /// <summary>
    /// 聊天窗口中显示了这些消息（打开会话、加载历史、收到新消息）：调用 zq_on_messages_shown（不等待），
    /// 插件可以重新附加自己保存的结果（例如翻译）。
    /// </summary>
    public void NotifyMessagesShown(ZulipSession session, IReadOnlyList<long> messageIds)
    {
        if (messageIds.Count == 0 || !_plugins.Exists(p => p.HandlesMessagesShown))
            return;
        var json = new JsonObject
        {
            ["account"] = session.Account.Id,
            ["messages"] = new JsonArray([.. messageIds.Select(id => (JsonNode)id)]),
        }.ToJsonString();
        _ = RunAsync(session, () =>
        {
            foreach (var p in _plugins.Where(p => p.HandlesMessagesShown))
                Invoke(p, () => p.CallMessagesShown(json));
            return true;
        });
    }

    /// <summary>插件快捷操作的绑定（<see cref="PluginShortcuts"/>）。</summary>
    public IReadOnlyDictionary<string, string> Shortcuts => _shortcuts;

    public void SaveShortcuts(Dictionary<string, string> bindings)
    {
        _shortcuts = bindings;
        if (_appSettings is not null)
            PluginShortcuts.Save(_appSettings, bindings);
    }

    /// <summary>鼠标手势绑定了消息菜单项。</summary>
    public bool HasShortcut(string gesture) => PluginShortcuts.Find(_plugins, _shortcuts, gesture) is not null;

    /// <summary>按下键盘快捷键：绑定了插件菜单项时调用它（与点击插件菜单相同）并返回 true。</summary>
    public bool InvokeKeyShortcut(string gesture)
    {
        if (PluginShortcuts.FindMenu(_plugins, _shortcuts, gesture) is not { } found)
            return false;
        InvokeMenu(found.Plugin, found.Menu.Id);
        return true;
    }

    /// <summary>对消息使用手势：有绑定的菜单项时调用它（与点击消息菜单相同）并返回 true。</summary>
    public bool InvokeShortcut(string gesture, ChatMessage message, ZulipSession session)
    {
        if (PluginShortcuts.Find(_plugins, _shortcuts, gesture) is not { } found)
            return false;
        InvokeMessageMenu(found.Plugin, found.Menu.Id, message, session);
        return true;
    }

    /// <summary>插件通过 set_settings_feedback 回复设置窗口（UI 线程）。</summary>
    public event EventHandler<(NativePlugin Plugin, PluginSettingsFeedback Feedback)>? SettingsFeedback;

    internal void RaiseSettingsFeedback(NativePlugin plugin, PluginSettingsFeedback feedback) => SettingsFeedback?.Invoke(this, (plugin, feedback));

    /// <summary>
    /// 设置中的按钮（不等待）：调用 zq_on_settings_button，<paramref name="json"/> 的格式见头文件。
    /// 返回 false 表示插件未开启或不支持。
    /// </summary>
    public bool InvokeSettingsButton(NativePlugin plugin, string json)
    {
        if (!plugin.CanUseSettingsButtons)
            return false;
        _ = RunAsync(DefaultSession(plugin), () => Invoke(plugin, () => plugin.CallSettingsButton(json)));
        return true;
    }

    /// <summary>把其他插件的请求交给发布资源的插件（不等待，任何线程）；回复由 <see cref="PluginResources"/> 等待。</summary>
    internal void InvokeResourceRequest(NativePlugin provider, long requestId, string json) =>
        _ = RunAsync(DefaultSession(provider), () => Invoke(provider, () => provider.CallResourceRequest(requestId, json)));

    /// <summary>插件菜单（不等待）。</summary>
    public void InvokeMenu(NativePlugin plugin, int menuId)
    {
        if (!plugin.Enabled || !plugin.IsLoaded || plugin.IsUnresponsive)
            return;
        _ = RunAsync(DefaultSession(plugin), () => Invoke(plugin, () => plugin.CallMenu(menuId)));
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
        var result = await RunChainAsync("filter_incoming", session, p => p.CallFilterIncoming(snapshot));
        return result == ChainResult.Block;
    }

    /// <summary>新消息回调（不等待结果）。</summary>
    public void DispatchMessage(ChatMessage message, ZulipSession session)
    {
        if (!AnyHandles("on_message"))
            return;
        var snapshot = PluginMessage.From(message, session);
        _ = RunChainAsync("on_message", session, p => p.CallOnMessage(snapshot));
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
        var accountId = session.Account.Id;
        var result = await RunChainAsync("filter_outgoing", session, p =>
        {
            var (r, changed) = CallOutgoing(p, conv, current, accountId);
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
            await RunAsync(DefaultSession(plugin), () =>
            {
                plugin.Load();
                Activate(plugin);
                return true;
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
    private async Task<ChainResult> RunChainAsync(string eventName, ZulipSession session, Func<NativePlugin, int> invoke)
    {
        var candidates = _plugins.Where(p => p.Handles(eventName))
            .OrderBy(p => p.Priority(eventName)).ThenBy(p => p.Id, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0 || _stuckCall is not null)
            return ChainResult.Ignore;

        var cancel = new CancellationTokenSource();
        var token = cancel.Token;
        var task = RunAsync(session, () =>
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
        // 排在同一个慢调用后面的多条事件链只计一次超时
        var call = Interlocked.Read(ref _currentCall);
        var stuck = _current;
        Log.Warn($"插件事件 {eventName} 超时：{stuck?.Id ?? "未知"}");
        if (stuck is not null && call != _chargedCall)
        {
            _chargedCall = call;
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

    /// <summary>当前是否在插件线程上。</summary>
    internal bool IsPluginThread => _thread.IsCurrent;

    /// <summary>
    /// 插件线程上正在执行的回调所属的会话（没有账号登录时为 null）。回调中 select_account 可以换成其他账号，只在本次回调内有效。
    /// </summary>
    internal static ZulipSession? CallSession
    {
        get => _callSession;
        set => _callSession = value;
    }

    // 投递到插件线程，执行期间记录所属的会话
    private Task<T> RunAsync<T>(ZulipSession? session, Func<T> f) => _thread.RunAsync(() =>
    {
        _callSession = session;
        try
        {
            return f();
        }
        finally
        {
            _callSession = null;
        }
    });

    // 插件线程上：会话进行中且插件已启用、已加载时，补发 zq_on_start、zq_on_enable
    private void Activate(NativePlugin plugin)
    {
        if (_callSession is not { } session || !_sessions.Contains(session) || !plugin.Enabled || !plugin.IsLoaded)
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
        Interlocked.Increment(ref _currentCall);
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
    private static unsafe (int Result, string? Changed) CallOutgoing(NativePlugin plugin, PluginConversation conv, string content,
        long accountId)
    {
        using var scope = new Utf8Scope();
        var native = new ZqOutgoing
        {
            StructSize = (uint)sizeof(ZqOutgoing), Conv = conv.ToNative(scope), Content = scope.Str(content), AccountId = accountId,
        };
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
