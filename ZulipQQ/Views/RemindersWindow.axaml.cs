using System;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.Controls;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>消息提醒窗口：只允许打开一个，再次打开时切换到对应标签。</summary>
public partial class RemindersWindow : Window, ISessionWindow
{
    private static RemindersWindow? _current;

    private RemindersViewModel? _vm;

    public RemindersWindow()
    {
        InitializeComponent();
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>打开消息提醒窗口并切换到 <paramref name="tab"/>；已打开时还原并激活。</summary>
    public static void ShowSingle(Window? owner, MainViewModel main, ReminderTab tab = ReminderTab.Pending)
    {
        // 打开中的是其他账号的窗口：关闭后为这个账号重新打开
        if (_current is { } other && SessionWindow.Of(other) != main.Session)
        {
            _current = null;
            other.Close();
        }
        if (_current is { _vm: { } existing })
        {
            WindowPlacement.BringToFront(_current);
            existing.SelectTabCommand.Execute(tab);
            return;
        }

        var vm = new RemindersViewModel(main.Session, main);
        var window = new RemindersWindow { _vm = vm };
        SessionWindow.Bind(window, main.Session);
        window.DataContext = vm;
        vm.MainWindowRequested += OnMainWindowRequested;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
        vm.Start(tab);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (_vm is not null)
        {
            _vm.MainWindowRequested -= OnMainWindowRequested;
            _vm.Close();
        }
        if (_current == this)
            _current = null;
    }

    private static void OnMainWindowRequested(object? sender, EventArgs e) => App.Current.ShowMainWindow();

    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (_vm is not { } vm)
            return;
        e.Handled = true;
        _ = vm.HandleLinkAsync(e.Target);
    }

    // Esc 关闭窗口；有对话框时由对话框自己处理
    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { Dialogs.Count: 0 })
            return;
        e.Handled = true;
        Close();
    }
}
