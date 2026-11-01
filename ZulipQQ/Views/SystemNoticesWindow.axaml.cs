using System;
using Avalonia.Controls;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>经典模式下的系统通知窗口：只允许打开一个。</summary>
public partial class SystemNoticesWindow : Window, ISessionWindow
{
    private static SystemNoticesWindow? _current;

    private SystemNoticesViewModel? _vm;

    public SystemNoticesWindow()
    {
        InitializeComponent();
    }

    /// <summary>打开系统通知窗口；已打开时还原并激活。</summary>
    public static void ShowSingle(Window? owner, MainViewModel main)
    {
        // 打开中的是其他账号的窗口：关闭后为这个账号重新打开
        if (_current is { } other && SessionWindow.Of(other) != main.Session)
        {
            _current = null;
            other.Close();
        }
        if (_current is not null)
        {
            WindowPlacement.BringToFront(_current);
            return;
        }

        var vm = new SystemNoticesViewModel(main.Session, main);
        var window = new SystemNoticesWindow { _vm = vm };
        SessionWindow.Bind(window, main.Session);
        window.DataContext = vm;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _vm?.Close();
        if (_current == this)
            _current = null;
    }
}
