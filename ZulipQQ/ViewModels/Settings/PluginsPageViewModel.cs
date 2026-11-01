using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
        RefreshShortcuts();
    }

    /// <summary>已开启的插件可以绑定鼠标手势的消息菜单项（对单条消息有效）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessageShortcuts))]
    public partial IReadOnlyList<PluginShortcutViewModel> MessageShortcuts { get; private set; } = [];

    public bool HasMessageShortcuts => MessageShortcuts.Count > 0;

    /// <summary>已开启的插件可以绑定键盘快捷键的插件菜单项（在 ZulipQQ 的窗口中有效）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasKeyShortcuts))]
    public partial IReadOnlyList<PluginShortcutViewModel> KeyShortcuts { get; private set; } = [];

    public bool HasKeyShortcuts => KeyShortcuts.Count > 0;

    private void RefreshShortcuts()
    {
        var bindings = PluginHost.Instance.Shortcuts;
        var plugins = PluginHost.Instance.Plugins;
        MessageShortcuts = plugins.SelectMany(p => PluginShortcuts.Bindable(p.ActiveMessageMenus)
                .Select(m => new PluginShortcutViewModel(this, PluginShortcuts.KeyOf(p, m), $"{p.Name} · {m.Name}", true,
                    PluginShortcuts.Effective(bindings, p, m))))
            .ToList();
        KeyShortcuts = plugins.SelectMany(p => PluginShortcuts.Bindable(p.ActiveMenus)
                .Select(m => new PluginShortcutViewModel(this, PluginShortcuts.KeyOf(p, m, messageMenu: false), $"{p.Name} · {m.Name}", false,
                    PluginShortcuts.Effective(bindings, p, m, messageMenu: false))))
            .ToList();
    }

    /// <summary>
    /// 录制了新的手势 / 快捷键：宿主已经使用的鼠标手势不能绑定；同一个手势原来绑定的其他项改为不绑定。
    /// 列表在录制框的这次事件处理完之后重建（被拒绝时恢复原来的值）。
    /// </summary>
    internal void SetShortcut(PluginShortcutViewModel item, string gesture)
    {
        if (gesture.Length > 0 && item.IsMouse && !PluginShortcuts.IsAllowedMouse(gesture))
        {
            Owner.ShowToast($"{PluginShortcuts.MouseLabel(gesture)}已用于选中消息、打开资料卡或右键菜单，请加上 Ctrl / Alt / Shift");
        }
        else
        {
            var bindings = new Dictionary<string, string>(PluginHost.Instance.Shortcuts);
            if (gesture.Length > 0)
            {
                foreach (var other in (item.IsMouse ? MessageShortcuts : KeyShortcuts).Where(o => o != item && o.Gesture == gesture))
                {
                    bindings[other.Key] = "";
                    Owner.ShowToast($"已取消“{other.Title}”的绑定");
                }
            }
            bindings[item.Key] = gesture;
            PluginHost.Instance.SaveShortcuts(bindings);
        }
        Avalonia.Threading.Dispatcher.UIThread.Post(RefreshShortcuts);
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

    /// <summary>
    /// 切换开关。依赖的插件未开启时提示先开启它们；开启尚未确认（新插件或文件已变化）的插件时先显示开启确认，取消时开关恢复原状。
    /// </summary>
    internal async void SetEnabled(PluginItemViewModel item, bool enabled)
    {
        var plugin = item.Plugin;
        try
        {
            if (enabled && MissingDependencies(plugin) is { Count: > 0 } missing)
            {
                Owner.ShowToast($"请先开启依赖的插件：{string.Join("、", missing)}");
                item.Refresh();
                return;
            }
            if (enabled && !plugin.IsApproved)
            {
                if (plugin.Fingerprint is null)
                {
                    Owner.ShowToast("无法读取插件文件");
                }
                else if (await ConfirmApprovalAsync(plugin) && !await PluginHost.Instance.ApproveAsync(plugin))
                {
                    Owner.ShowToast("插件文件已变化，请重启程序后再开启");
                }
                item.Refresh();
                return;
            }
            await PluginHost.Instance.SetEnabledAsync(plugin, enabled);
        }
        catch (Exception ex)
        {
            // 插件线程已停止（程序正在退出）
            Log.Warn($"切换插件状态失败：{plugin.Id}", ex);
        }
    }

    /// <summary>打开插件设置；保存后通知插件。</summary>
    internal void ShowSettings(NativePlugin plugin)
    {
        PluginSettingsDialogViewModel dialog = null!;
        dialog = new PluginSettingsDialogViewModel(plugin, PluginHost.Instance.LoadSettings(plugin), Owner.Dialogs, values =>
        {
            Owner.Dialogs.Remove(dialog);
            if (values is null)
                return;
            try
            {
                PluginHost.Instance.SaveSettings(plugin, values);
                Owner.ShowToast("已保存");
            }
            catch (Exception ex)
            {
                Log.Warn($"保存插件设置失败：{plugin.Id}", ex);
                Owner.ShowToast("保存失败");
            }
        });
        Owner.Dialogs.Add(dialog);
    }

    // 未开启（或未安装）的依赖插件的名称
    private static List<string> MissingDependencies(NativePlugin plugin) =>
        (plugin.Manifest?.Dependencies ?? []).Select(id => PluginHost.Instance.Plugins.FirstOrDefault(p => p.Id == id) is { } dep
            ? dep.Enabled ? null : dep.Manifest?.Name ?? id
            : $"{id}（未安装）").OfType<string>().ToList();

    private Task<bool> ConfirmApprovalAsync(NativePlugin plugin)
    {
        var tcs = new TaskCompletionSource<bool>();
        PluginApprovalDialogViewModel dialog = null!;
        dialog = new PluginApprovalDialogViewModel(plugin, confirmed =>
        {
            Owner.Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        });
        Owner.Dialogs.Add(dialog);
        return tcs.Task;
    }

    public override void Close() => PluginHost.Instance.Changed -= OnHostChanged;

    private void OnHostChanged(object? sender, EventArgs e)
    {
        foreach (var item in Items)
            item.Refresh();
        RefreshShortcuts();
    }
}

/// <summary>插件列表的一行。</summary>
public sealed partial class PluginItemViewModel : ObservableObject
{
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

    /// <summary>异常状态（开启、停用由开关表示，不显示文字）。</summary>
    public string StatusText => Plugin switch
    {
        { Manifest: null } or { Enabled: true, IsLoaded: false, LoadError: not null } => $"加载失败：{Plugin.LoadError}",
        { Fingerprint: null } => "无法读取插件文件",
        { NeedsReapproval: true } => "插件文件已变化（更新或被修改），需要重新开启",
        { Enabled: true, IsUnresponsive: true } => "无响应",
        _ => "",
    };

    public bool HasStatus => StatusText.Length > 0;

    public bool IsError => Plugin.Manifest is null || Plugin.IsUnresponsive || Plugin.NeedsReapproval
        || (Plugin.Enabled && Plugin.LoadError is not null);

    public bool CanToggle => Plugin.Manifest is not null;

    public bool IsEnabled
    {
        get => Plugin.Enabled;
        set
        {
            if (value != Plugin.Enabled)
                _owner.SetEnabled(this, value);
        }
    }

    /// <summary>插件声明了设置项（停用时也可以修改）。</summary>
    public bool HasSettings => Plugin.Manifest is { Settings.Count: > 0 };

    [RelayCommand]
    private void ShowSettings() => _owner.ShowSettings(Plugin);

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

/// <summary>快捷操作的一行：左侧为录制框，右侧为“插件名 · 菜单项”。</summary>
public sealed class PluginShortcutViewModel(PluginsPageViewModel owner, string key, string title, bool isMouse, string gesture)
{
    public string Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>鼠标手势（消息菜单）；否则为键盘快捷键（插件菜单）。</summary>
    public bool IsMouse { get; } = isMouse;

    /// <summary>当前绑定的手势（空字符串为不绑定）；录制框写入新值时交给页面处理。</summary>
    public string Gesture
    {
        get => gesture;
        set
        {
            if (value != gesture)
                owner.SetShortcut(this, value);
        }
    }
}
