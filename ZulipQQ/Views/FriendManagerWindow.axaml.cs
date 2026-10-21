using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class FriendManagerWindow : Window
{
    // 好友管理器只允许打开一个
    private static FriendManagerWindow? _current;

    public FriendManagerWindow()
    {
        InitializeComponent();
    }

    /// <summary>打开好友管理器；已打开时把它还原并激活。</summary>
    public static void ShowSingle(Window owner)
    {
        if (_current is not null)
        {
            if (_current.WindowState == WindowState.Minimized)
                _current.WindowState = WindowState.Normal;
            _current.Activate();
            return;
        }

        _current = new FriendManagerWindow { DataContext = new FriendManagerViewModel() };
        _current.Show(owner);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        (DataContext as FriendManagerViewModel)?.Detach();
        if (_current == this)
            _current = null;
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();
}
