using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.Plugins;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>插件日志窗口：每个插件最多一个，再次打开时激活。</summary>
public partial class PluginLogWindow : Window, ISessionWindow
{
    private static readonly Dictionary<NativePlugin, PluginLogWindow> Open = [];

    private PluginLogViewModel? _vm;

    public PluginLogWindow()
    {
        InitializeComponent();
        CopyButton.Click += OnCopyClick;
    }

    public static void ShowFor(NativePlugin plugin, Window? owner)
    {
        if (Open.TryGetValue(plugin, out var existing))
        {
            WindowPlacement.BringToFront(existing);
            return;
        }
        var vm = new PluginLogViewModel(plugin);
        var window = new PluginLogWindow { DataContext = vm, _vm = vm };
        vm.Reloaded += window.OnReloaded;
        Open[plugin] = window;
        window.Closed += (_, _) => Open.Remove(plugin);
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        window.OnReloaded(vm, EventArgs.Empty);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.Reloaded -= OnReloaded;
            _vm.Close();
        }
    }

    // 新日志加入后滚动到底部
    private void OnReloaded(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(LogScroller.ScrollToEnd, DispatcherPriority.Background);

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null || Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(_vm.AllText);
        }
        catch (Exception ex)
        {
            Log.Warn("复制插件日志失败", ex);
        }
    }
}
