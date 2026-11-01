using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// 开启插件前的确认：说明插件可以读取什么、可以做什么、会自动打开哪些链接 / 程序，以及原生插件不受沙箱限制。
/// 关闭时调用 onClosed：开启传入 true，取消或所在窗口关闭传入 false。
/// </summary>
public sealed partial class PluginApprovalDialogViewModel : ViewModelBase, IDisposable
{
    private static readonly Dictionary<string, string> EventNames = new()
    {
        ["on_message"] = "接收新消息",
        ["filter_incoming"] = "拦截收到的消息",
        ["filter_outgoing"] = "发送前检查消息",
    };

    private readonly Action<bool> _onClosed;

    public PluginApprovalDialogViewModel(NativePlugin plugin, Action<bool> onClosed)
    {
        _onClosed = onClosed;
        var m = plugin.Manifest!;
        Title = $"开启插件“{plugin.Name}”";
        Meta = string.Join(" · ", new[] { m.Version, m.Author, m.Id }.Where(s => !string.IsNullOrEmpty(s)));
        Notice = plugin.NeedsReapproval ? "插件文件与上次开启时不同（已更新或被修改），请重新确认。" : null;

        Reads = Names(m, PermissionKind.Read);
        Acts = Names(m, PermissionKind.Act);
        Declared = Names(m, PermissionKind.Declared);
        Events = m.Events.Keys.Where(e => plugin.HasPermission(PluginManifest.EventPermissions[e]))
            .Select(e => EventNames.GetValueOrDefault(e, e))
            .Concat(plugin.HasPermission("read_message")
                ? m.MessageMenus.Select(menu => $"消息右键菜单“{menu.Name}”")
                : [])
            .ToList();
        LaunchRules = m.Permissions.Contains("launch") ? m.Launch.Select(r => r.Pattern).ToList() : [];
        Dependencies = m.Dependencies.Select(id => PluginHost.Instance.Plugins.FirstOrDefault(p => p.Id == id)?.Manifest?.Name ?? id)
            .ToList();
    }

    public string Title { get; }

    public string Meta { get; }

    /// <summary>插件更新后的提示；首次开启为 null。</summary>
    public string? Notice { get; }

    public bool HasNotice => Notice is not null;

    public IReadOnlyList<string> Reads { get; }

    public bool HasReads => Reads.Count > 0;

    public IReadOnlyList<string> Acts { get; }

    public bool HasActs => Acts.Count > 0;

    public IReadOnlyList<string> Declared { get; }

    public bool HasDeclared => Declared.Count > 0;

    public IReadOnlyList<string> Events { get; }

    public bool HasEvents => Events.Count > 0;

    /// <summary>白名单：开启后插件可以不经点击直接打开。</summary>
    public IReadOnlyList<string> LaunchRules { get; }

    public bool HasLaunchRules => LaunchRules.Count > 0;

    /// <summary>依赖的插件。</summary>
    public IReadOnlyList<string> Dependencies { get; }

    public bool HasDependencies => Dependencies.Count > 0;

    public bool HasNoPermissions => !HasReads && !HasActs && !HasDeclared;

    [RelayCommand]
    private void Confirm() => _onClosed(true);

    [RelayCommand]
    private void Cancel() => _onClosed(false);

    public void Dispose() => _onClosed(false);

    private static List<string> Names(PluginManifest m, PermissionKind kind) =>
        PermissionCatalog.All.Where(p => p.Kind == kind && m.Permissions.Contains(p.Id)).Select(p => p.Name).ToList();
}
