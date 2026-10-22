using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>插件：已安装插件的列表、启用开关、插件菜单与日志。</summary>
public sealed partial class PluginsPageViewModel : SettingsPageViewModel
{
    public PluginsPageViewModel(SettingsViewModel owner) : base(owner)
    {
        PluginHost.Instance.Changed += OnHostChanged;
        foreach (var plugin in PluginHost.Instance.Plugins)
            Items.Add(new PluginItemViewModel(plugin, this));
    }

    public ObservableCollection<PluginItemViewModel> Items { get; } = [];

    public bool HasItems => Items.Count > 0;

    /// <summary>请求打开插件日志窗口（由视图处理）。</summary>
    public event EventHandler<NativePlugin>? LogRequested;

    [RelayCommand]
    private void OpenFolder()
    {
        var dir = PluginHost.PluginsDirectory;
        try
        {
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开文件夹失败：{dir}", ex);
            Owner.ShowToast("无法打开文件夹");
        }
    }

    internal void ShowLog(NativePlugin plugin) => LogRequested?.Invoke(this, plugin);

    internal async void SetEnabled(NativePlugin plugin, bool enabled)
    {
        try
        {
            await PluginHost.Instance.SetEnabledAsync(plugin, enabled);
        }
        catch (Exception ex)
        {
            // 插件线程已停止（程序正在退出）
            Log.Warn($"切换插件状态失败：{plugin.Id}", ex);
        }
    }

    public override void Close() => PluginHost.Instance.Changed -= OnHostChanged;

    private void OnHostChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
            item.Refresh();
    }
}

/// <summary>插件列表的一行。</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
    private static readonly Dictionary<string, string> PermissionNames = new()
    {
        ["read_message"] = "读取消息",
        ["send_message"] = "发送消息",
        ["read_contacts"] = "读取联系人和频道",
        ["modify_message"] = "修改消息状态、改写发送内容",
        ["upload_file"] = "上传文件",
    };

    private readonly PluginsPageViewModel _owner;

    public PluginItemViewModel(NativePlugin plugin, PluginsPageViewModel owner)
    {
        Plugin = plugin;
        _owner = owner;
        Menus = plugin.Manifest?.Menus.Select(m => new PluginMenuItemViewModel(plugin, m)).ToList() ?? [];
    }

    public NativePlugin Plugin { get; }

    public string Name => Plugin.Name;

    /// <summary>版本与作者，例如“1.0.0 · ZulipQQ”。</summary>
    public string Meta => Plugin.Manifest is { } m
        ? string.Join(" · ", new[] { m.Version, m.Author }.Where(s => !string.IsNullOrEmpty(s)))
        : Plugin.Id;

    public string Description => Plugin.Manifest?.Description ?? "";

    public bool HasDescription => Description.Length > 0;

    public string PermissionsText => Plugin.Manifest is { Permissions.Count: > 0 } m
        ? "权限：" + string.Join("、", m.Permissions.Select(p => PermissionNames.GetValueOrDefault(p, p)))
        : "权限：无";

    public string StatusText => Plugin switch
    {
        { Manifest: null } or { Enabled: true, IsLoaded: false, LoadError: not null } => $"加载失败：{Plugin.LoadError}",
        { Enabled: false } => "已停用",
        { IsUnresponsive: true } => "无响应",
        { IsLoaded: false } => "正在加载…",
        _ => "已启用",
    };

    public bool IsError => Plugin.Manifest is null || Plugin.IsUnresponsive || (Plugin.Enabled && Plugin.LoadError is not null);

    public bool CanToggle => Plugin.Manifest is not null;

    public bool IsEnabled
    {
        get => Plugin.Enabled;
        set
        {
            if (value != Plugin.Enabled)
                _owner.SetEnabled(Plugin, value);
        }
    }

    public IReadOnlyList<PluginMenuItemViewModel> Menus { get; }

    /// <summary>插件已加载并启用时才显示菜单。</summary>
    public bool ShowMenus => Menus.Count > 0 && Plugin is { Enabled: true, IsLoaded: true };

    [RelayCommand]
    private void ShowLog() => _owner.ShowLog(Plugin);

    public void Refresh() => OnPropertyChanged(string.Empty);
}

/// <summary>插件声明的一个菜单（显示为按钮）。</summary>
public sealed partial class PluginMenuItemViewModel(NativePlugin plugin, PluginMenu menu)
{
    public string Name => menu.Name;

    [RelayCommand]
    private void Invoke() => PluginHost.Instance.InvokeMenu(plugin, menu.Id);
}
