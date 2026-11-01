using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

/// <summary>搜索结果“更多”窗口：只允许打开一个，再次打开时换成新的结果。点击一条结果打开它并关闭窗口。</summary>
public partial class SearchMoreWindow : Window, ISessionWindow
{
    private static SearchMoreWindow? _current;

    public SearchMoreWindow()
    {
        InitializeComponent();
        // 点击结果：按钮的命令执行之后再关闭（标题栏的按钮不处理）
        AddHandler(Button.ClickEvent, (_, e) =>
        {
            if (e.Source is Button button && button.Classes.Contains("result"))
                Dispatcher.UIThread.Post(Close);
        }, RoutingStrategies.Bubble);
        AddHandler(KeyDownEvent, OnEscapeKeyDown, RoutingStrategies.Bubble);
    }

    /// <summary>显示 <paramref name="section"/> 的全部结果；已打开时换成新的结果并激活。</summary>
    /// <param name="owner">主窗口：搜索结果属于它正在显示的账号。</param>
    public static void ShowSingle(Window? owner, SearchSection section, string query)
    {
        var vm = new SearchMoreViewModel(section, query);
        var session = SessionWindow.Of(owner);
        if (_current is not null)
        {
            if (session is not null)
                SessionWindow.Bind(_current, session);
            _current.DataContext = vm;
            WindowPlacement.BringToFront(_current);
            return;
        }

        var window = new SearchMoreWindow();
        if (session is not null)
            SessionWindow.Bind(window, session);
        window.DataContext = vm;
        _current = window;
        WindowPlacement.CenterOn(window, owner);
        window.Show();
    }

    protected override void OnClosed(System.EventArgs e)
    {
        base.OnClosed(e);
        if (_current == this)
            _current = null;
    }

    // Esc 关闭窗口
    private void OnEscapeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        e.Handled = true;
        Close();
    }
}
