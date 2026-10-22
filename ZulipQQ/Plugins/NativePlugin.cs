using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace ZulipQQ.Plugins;

/// <summary>插件日志的一条。</summary>
public sealed record PluginLogEntry(DateTime Time, int Level, string Category, string Text);

/// <summary>
/// 一个原生插件：清单、库句柄、导出函数、传给插件的宿主函数表与日志。
/// 导出函数只在插件线程上调用；<see cref="Enabled"/> 由 UI 线程修改，插件线程读取。
/// DLL 加载后不会卸载（停用只是不再派发事件）。
/// </summary>
public sealed unsafe class NativePlugin
{
    private const int MaxLogs = 1000;

    private readonly List<PluginLogEntry> _logs = [];
    private volatile bool _enabled;
    private volatile bool _unresponsive;
    private int _timeouts;
    private ZqHostApi* _api;
    private GCHandle _self;

    public NativePlugin(string directory, PluginManifest? manifest, string? loadError)
    {
        DirectoryPath = directory;
        Manifest = manifest;
        LoadError = loadError;
    }

    /// <summary>插件目录（<c>plugins\&lt;id&gt;</c>）。</summary>
    public string DirectoryPath { get; }

    /// <summary>清单；plugin.json 无效时为 null（此时 <see cref="LoadError"/> 说明原因）。</summary>
    public PluginManifest? Manifest { get; }

    public string Id => Manifest?.Id ?? Path.GetFileName(DirectoryPath);

    public string Name => Manifest?.Name ?? Path.GetFileName(DirectoryPath);

    /// <summary>清单无效、DLL 加载失败或 zq_plugin_init 失败的原因。</summary>
    public string? LoadError { get; private set; }

    /// <summary>已加载并成功初始化。</summary>
    public bool IsLoaded { get; private set; }

    public bool Enabled
    {
        get => _enabled;
        internal set => _enabled = value;
    }

    /// <summary>连续多次超时：本次运行内不再派发事件。</summary>
    public bool IsUnresponsive => _unresponsive;

    internal delegate* unmanaged[Cdecl]<int> OnStart { get; private set; }
    internal delegate* unmanaged[Cdecl]<int> OnExit { get; private set; }
    internal delegate* unmanaged[Cdecl]<int> OnEnable { get; private set; }
    internal delegate* unmanaged[Cdecl]<int> OnDisable { get; private set; }
    internal delegate* unmanaged[Cdecl]<ZqMessage*, int> OnMessage { get; private set; }
    internal delegate* unmanaged[Cdecl]<ZqMessage*, int> FilterIncoming { get; private set; }
    internal delegate* unmanaged[Cdecl]<ZqOutgoing*, int> FilterOutgoing { get; private set; }
    internal delegate* unmanaged[Cdecl]<int, int> OnMenu { get; private set; }

    /// <summary>有新日志（可能在任何线程上触发）。</summary>
    public event EventHandler? LogsChanged;

    public bool HasPermission(string permission) => Manifest?.Permissions.Contains(permission) == true;

    /// <summary>接收该事件：已加载、已启用、未被标记为无响应、导出了函数、声明了事件并拥有所需权限。</summary>
    public bool Handles(string eventName) =>
        IsLoaded && Enabled && !IsUnresponsive && Manifest is { } m && m.Events.ContainsKey(eventName)
        && HasPermission(PluginManifest.EventPermissions[eventName]) && eventName switch
        {
            "on_message" => OnMessage != null,
            "filter_incoming" => FilterIncoming != null,
            "filter_outgoing" => FilterOutgoing != null,
            _ => false,
        };

    public int Priority(string eventName) => Manifest?.Events.GetValueOrDefault(eventName, PluginManifest.MaxPriority) ?? PluginManifest.MaxPriority;

    /// <summary>
    /// 在插件线程上调用：加载 DLL、准备宿主函数表并调用 zq_plugin_init。失败时记录 <see cref="LoadError"/>。
    /// 已加载或清单无效时不做任何事。
    /// </summary>
    internal void Load()
    {
        if (IsLoaded || Manifest is not { } manifest)
            return;
        try
        {
            var handle = NativeLibrary.Load(Path.Combine(DirectoryPath, manifest.Library));
            var init = (delegate* unmanaged[Cdecl]<ZqHostApi*, int>)NativeLibrary.GetExport(handle, "zq_plugin_init");
            _self = GCHandle.Alloc(this);
            _api = (ZqHostApi*)NativeMemory.AllocZeroed((nuint)sizeof(ZqHostApi));
            HostApi.Fill(_api, GCHandle.ToIntPtr(_self));

            OnStart = (delegate* unmanaged[Cdecl]<int>)Export(handle, "zq_on_start");
            OnExit = (delegate* unmanaged[Cdecl]<int>)Export(handle, "zq_on_exit");
            OnEnable = (delegate* unmanaged[Cdecl]<int>)Export(handle, "zq_on_enable");
            OnDisable = (delegate* unmanaged[Cdecl]<int>)Export(handle, "zq_on_disable");
            OnMessage = (delegate* unmanaged[Cdecl]<ZqMessage*, int>)Export(handle, "zq_on_message");
            FilterIncoming = (delegate* unmanaged[Cdecl]<ZqMessage*, int>)Export(handle, "zq_filter_incoming");
            FilterOutgoing = (delegate* unmanaged[Cdecl]<ZqOutgoing*, int>)Export(handle, "zq_filter_outgoing");
            OnMenu = (delegate* unmanaged[Cdecl]<int, int>)Export(handle, "zq_on_menu");

            var result = init(_api);
            if (result != Zq.Ok)
            {
                LoadError = $"初始化失败（zq_plugin_init 返回 {result}）";
                return;
            }
            IsLoaded = true;
            LoadError = null;
        }
        catch (Exception ex)
        {
            LoadError = ex switch
            {
                DllNotFoundException => $"无法加载 {manifest.Library}：{ex.Message}",
                EntryPointNotFoundException => "缺少导出函数 zq_plugin_init",
                BadImageFormatException => $"{manifest.Library} 不是 x64 DLL",
                _ => ex.Message,
            };
            Services.Log.Warn($"加载插件失败：{manifest.Id}", ex);
        }
    }

    // 以下在插件线程上调用导出函数；没有导出的函数视为返回 ZQ_OK / IGNORE
    internal int CallStart() => OnStart == null ? Zq.Ok : OnStart();
    internal int CallExit() => OnExit == null ? Zq.Ok : OnExit();
    internal int CallEnable() => OnEnable == null ? Zq.Ok : OnEnable();
    internal int CallDisable() => OnDisable == null ? Zq.Ok : OnDisable();
    internal int CallMenu(int menuId) => OnMenu == null ? Zq.Ok : OnMenu(menuId);
    internal int CallOnMessage(PluginMessage message) => CallMessage(OnMessage, message);
    internal int CallFilterIncoming(PluginMessage message) => CallMessage(FilterIncoming, message);

    // 消息快照编码为 zq_message，只在调用期间有效
    private static int CallMessage(delegate* unmanaged[Cdecl]<ZqMessage*, int> export, PluginMessage message)
    {
        if (export == null)
            return Zq.EventIgnore;
        using var scope = new Utf8Scope();
        var native = message.ToNative(scope);
        return export(&native);
    }

    private static nint Export(nint handle, string name) => NativeLibrary.TryGetExport(handle, name, out var p) ? p : 0;

    /// <summary>调用超时（UI 线程）：连续 3 次标记为无响应。</summary>
    internal void OnTimeout()
    {
        if (Interlocked.Increment(ref _timeouts) >= 3)
            _unresponsive = true;
    }

    /// <summary>调用正常返回（插件线程）：超时计数清零。</summary>
    internal void OnReturned() => Interlocked.Exchange(ref _timeouts, 0);

    public void AddLog(int level, string category, string text)
    {
        lock (_logs)
        {
            _logs.Add(new PluginLogEntry(DateTime.Now, level, category, text));
            if (_logs.Count > MaxLogs)
                _logs.RemoveRange(0, _logs.Count - MaxLogs);
        }
        LogsChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<PluginLogEntry> GetLogs()
    {
        lock (_logs)
            return [.. _logs];
    }

    public void ClearLogs()
    {
        lock (_logs)
            _logs.Clear();
        LogsChanged?.Invoke(this, EventArgs.Empty);
    }
}
