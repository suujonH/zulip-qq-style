using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>消息气泡下方一个插件附加的内容：插件名（点击折叠 / 展开）、文字与按钮。</summary>
public sealed partial class PluginAnnotationViewModel : ViewModelBase
{
    private readonly long _accountId;
    private readonly long _messageId;

    public PluginAnnotationViewModel(NativePlugin plugin, PluginAnnotation annotation, long accountId, long messageId,
        Action<string> showToast, Action<string> fill)
    {
        Plugin = plugin;
        _accountId = accountId;
        _messageId = messageId;
        Text = annotation.Text;
        IsCollapsed = PluginAnnotationStore.Instance.IsCollapsed(accountId, messageId, plugin.Id);
        Actions = annotation.Actions.Select(a => new PluginActionViewModel(plugin, a, showToast, fill)).ToList();
    }

    public NativePlugin Plugin { get; }

    public string PluginName => Plugin.Name;

    public string Text { get; }

    public bool HasText => Text.Length > 0;

    public bool IsCollapsed { get; }

    public bool IsExpanded => !IsCollapsed;

    public bool HasActions => Actions.Count > 0;

    public IReadOnlyList<PluginActionViewModel> Actions { get; }

    public string ToggleToolTip => IsCollapsed ? $"展开 {PluginName} 附加的内容" : $"折叠 {PluginName} 附加的内容（不修改消息）";

    [RelayCommand]
    private void ToggleCollapsed() => PluginAnnotationStore.Instance.SetCollapsed(_accountId, _messageId, Plugin.Id, !IsCollapsed);
}

/// <summary>
/// 附加内容中的按钮：填入输入框（不发送），或打开链接 / 运行程序（用户点击即确认，不要求在白名单中）。
/// </summary>
public sealed partial class PluginActionViewModel(NativePlugin plugin, PluginAction action, Action<string> showToast,
    Action<string> fill) : ViewModelBase
{
    public string Label => action.Label;

    /// <summary>填入的文字，或完整的目标（程序带参数），显示在按钮下方与提示中。</summary>
    public string Detail => action.IsFill ? action.Fill : Launcher.Describe(action.Target, action.Args);

    public bool IsWhitelisted => !action.IsFill && Launcher.IsWhitelisted(plugin, action.Target);

    public string ToolTipText => action.IsFill ? "点击填入输入框（不会发送）：" + Detail
        : (IsWhitelisted ? "白名单内：" : "不在白名单中，点击后打开：") + Detail;

    /// <summary>插件仍然启用且声明了对应的权限（fill_composer 或 launch）。</summary>
    public bool CanRun => plugin.Enabled && plugin.HasPermission(action.IsFill ? "fill_composer" : "launch");

    [RelayCommand]
    private void Run()
    {
        if (!CanRun)
            return;
        if (action.IsFill)
        {
            fill(action.Fill);
            return;
        }
        try
        {
            Launcher.Launch(action.Target, action.Args);
            plugin.AddLog(Zq.LogInfo, "宿主", $"用户点击打开 {Detail}");
        }
        catch (Exception ex)
        {
            Log.Warn($"打开插件按钮失败：{Detail}", ex);
            showToast("无法打开：" + action.Target);
        }
    }
}
