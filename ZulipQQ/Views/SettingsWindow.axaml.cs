using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views;

/// <summary>设置窗口：只允许打开一个，再次打开时切换到对应页。</summary>
public partial class SettingsWindow : Window, ISessionWindow
{
    private static SettingsWindow? _current;

    private SettingsViewModel? _vm;

    public SettingsWindow()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>打开设置窗口并切换到 <paramref name="page"/>；已打开时还原并激活。</summary>
    public static void ShowSingle(Window? owner, MainViewModel main, SettingsPage page = SettingsPage.General)
    {
        if (_current is { _vm: { } existing })
        {
            WindowPlacement.BringToFront(_current);
            existing.Navigate(page);
            return;
        }

        var vm = new SettingsViewModel(main.Session, main);
        var window = new SettingsWindow { DataContext = vm, _vm = vm };
        vm.PropertyChanged += window.OnVmPropertyChanged;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Navigate(page);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmPropertyChanged;
            _vm.Close();
        }
        if (_current == this)
            _current = null;
    }

    // 换页后回到顶部
    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SettingsViewModel.CurrentPage))
            PageScroller.Offset = default;
    }

    // Esc 关闭窗口；有对话框时先关闭对话框（由对话框自己处理）
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { Dialogs.Count: 0 })
            return;
        e.Handled = true;
        Close();
    }
}
