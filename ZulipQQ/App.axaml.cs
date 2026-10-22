using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZulipQQ.Data;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;
using ZulipQQ.Views;

namespace ZulipQQ;

public partial class App : Application
{
    private LoginWindow? _loginWindow;
    private bool _closingForLogin;
    private bool _endingSession;
    private TrayService? _tray;

    public static new App Current => (App)Application.Current!;

    /// <summary>正在退出账号或退出程序；各窗口的 Closing 处理器据此不再拦截关闭。</summary>
    public static bool IsEndingSession { get; private set; }

    private IClassicDesktopStyleApplicationLifetime Desktop =>
        (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 退出账号时会话的请求被取消：命令和事件处理器中漏出的取消异常不应使程序崩溃
            Dispatcher.UIThread.UnhandledException += (_, e) =>
            {
                if (e.Exception is OperationCanceledException)
                {
                    Log.Debug("忽略已取消的操作", e.Exception);
                    e.Handled = true;
                }
            };
            LocalDb.Instance = LocalDb.Open(AppPaths.DatabasePath);
            ThemeService.Initialize(LocalDb.Instance);
            // 登录窗与主窗口会互相替换，由代码决定何时退出
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            var last = new AccountRepository(LocalDb.Instance).GetAll().FirstOrDefault();
            var vm = ShowLogin(last, null);
            if (last is { AutoLogin: true })
                _ = vm.AutoLoginAsync(last);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 退出当前账号：关闭所有会话窗口、结束会话，回到登录窗（预填该账号）。
    /// 下次启动不再自动登录，但保留 key 以便直接登录。
    /// </summary>
    public async Task LogoutAsync(bool confirm, string? error = null)
    {
        if (_endingSession || ZulipSession.Current is not { } session)
            return;
        if (confirm && Desktop.MainWindow?.DataContext is MainViewModel main
                    && !await main.ConfirmAsync("确定要退出当前账号吗？"))
            return;
        if (_endingSession || ZulipSession.Current != session)
            return;

        _endingSession = true;
        IsEndingSession = true;
        try
        {
            var accounts = new AccountRepository(LocalDb.Instance!);
            accounts.SetAutoLogin(session.Account.Id, false);
            await CloseChatsAsync(session);
            CloseSessionWindows();
            DisposeTray();
            await session.DisposeAsync();
            // 登录窗使用默认外观与字号
            ThemeService.SetPreviewDark(false);
            ThemeService.ApplyColorScheme(1);
            ThemeService.ApplyFontSize(ThemeService.DefaultFontSize);
            ShowLogin(accounts.Get(session.Account.Id) ?? session.Account, error);
        }
        finally
        {
            IsEndingSession = false;
            _endingSession = false;
        }
    }

    /// <summary>退出程序：结束会话（不修改自动登录）后关闭。</summary>
    public async Task ExitAsync()
    {
        if (_endingSession)
            return;
        _endingSession = true;
        IsEndingSession = true;
        if (ZulipSession.Current is { } session)
            await CloseChatsAsync(session);
        CloseSessionWindows();
        DisposeTray();
        if (ZulipSession.Current is { } current)
            await current.DisposeAsync();
        Desktop.Shutdown();
    }

    /// <summary>
    /// 用户关闭主面板（标题栏 ×、Alt+F4 等）：设置为最小化到托盘且托盘图标可见时隐藏主面板，否则退出程序。
    /// </summary>
    public void RequestCloseMain()
    {
        if (ZulipSession.Current is { } session && _tray is { IsVisible: true }
            && session.Settings.Local.EffectiveCloseAction == CloseAction.MinimizeToTray)
            Desktop.MainWindow?.Hide();
        else
            _ = ExitAsync();
    }

    private LoginViewModel ShowLogin(AccountRow? prefill, string? error)
    {
        var vm = new LoginViewModel(new AccountRepository(LocalDb.Instance!), CompleteLoginAsync, prefill, error);
        var window = new LoginWindow { DataContext = vm };
        window.Closed += (_, _) =>
        {
            vm.Cancel();
            if (_loginWindow == window)
                _loginWindow = null;
            // 用户关闭登录窗且没有会话时退出程序；登录成功后由代码关闭时不退出
            if (!_closingForLogin && ZulipSession.Current is null)
                Desktop.Shutdown();
        };
        _loginWindow = window;
        Desktop.MainWindow = window;
        window.Show();
        return vm;
    }

    private async Task CompleteLoginAsync(AccountRow account, CancellationToken ct)
    {
        var session = await ZulipSession.CreateAsync(account, ct);
        if (ct.IsCancellationRequested)
        {
            await session.DisposeAsync();
            ct.ThrowIfCancellationRequested();
        }

        ApplySessionTheme(session);
        session.Settings.Changed += (_, name) =>
        {
            if (name == "color_scheme")
                ThemeService.ApplyColorScheme(session.Settings.Zulip.ColorScheme);
        };
        session.Settings.Local.Changed += (_, key) =>
        {
            if (key == "font_size")
                ThemeService.ApplyFontSize(session.Settings.Local.FontSize);
        };
        session.Resynced += (_, _) => ApplySessionTheme(session);

        var mainVm = new MainViewModel(session);
        var main = new MainWindow { DataContext = mainVm };
        Desktop.MainWindow = main;
        main.Show();
        session.Notifications.Requested += (_, request) => ShowNotification(main, mainVm, request);
        CreateTray(session, main);

        _closingForLogin = true;
        try
        {
            _loginWindow?.Close();
        }
        finally
        {
            _closingForLogin = false;
        }

        session.ConnectionChanged += (_, _) =>
        {
            if (session.Connection == ConnectionState.AuthFailed)
                _ = LogoutAsync(confirm: false, "登录已失效，请重新登录");
        };
        session.Start();
    }

    // 外观（Zulip 的 color_scheme）与本地字号
    private static void ApplySessionTheme(ZulipSession session)
    {
        ThemeService.ApplyColorScheme(session.Settings.Zulip.ColorScheme);
        ThemeService.ApplyFontSize(session.Settings.Local.FontSize);
    }

    // 主窗口不在前台时闪烁任务栏和托盘图标；需要时弹出通知，点击后把主窗口带到前台并打开会话
    private void ShowNotification(MainWindow main, MainViewModel vm, NotificationRequest request)
    {
        if (!main.IsActive)
        {
            TaskbarFlash.Start(main);
            _tray?.StartFlashing();
        }
        if (!request.Popup)
            return;
        NotificationPopupWindow.Show(request, () =>
        {
            ShowMain(main);
            _ = vm.OpenChatAsync(request.Key);
        });
    }

    /// <summary>显示并激活主面板（可能隐藏在托盘中）。附属窗口“定位到聊天”、转发等需要主面板时调用。</summary>
    public void ShowMainWindow()
    {
        if (Desktop.MainWindow is MainWindow main)
            ShowMain(main);
    }

    private static void ShowMain(Window main)
    {
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        main.Activate();
    }

    // 托盘图标：是否显示由本地设置决定；主面板被激活或没有未读消息时停止闪烁
    private void CreateTray(ZulipSession session, MainWindow main)
    {
        var tray = new TrayService($"ZulipQQ - {session.Users.Get(session.SelfId)?.FullName ?? session.Account.FullName}",
            () => ShowMain(main), () =>
            {
                // 确认框显示在主面板中
                ShowMain(main);
                _ = LogoutAsync(confirm: true);
            }, () => _ = ExitAsync())
        {
            IsVisible = session.Settings.Local.ShowTrayIcon,
        };
        _tray = tray;
        session.Settings.Local.Changed += (_, key) =>
        {
            if (key != LocalSettings.ShowTrayIconKey || _tray != tray)
                return;
            tray.IsVisible = session.Settings.Local.ShowTrayIcon;
            // 主面板隐藏在托盘中时去掉托盘图标，否则无法再打开主面板
            if (!tray.IsVisible && !main.IsVisible)
                ShowMain(main);
        };
        main.Activated += (_, _) => tray.StopFlashing();
        session.Unread.ConversationChanged += (_, _) =>
        {
            if (tray.IsFlashing && session.Unread.TotalForBadge(session.Conversations.GetLevel) == 0)
                tray.StopFlashing();
        };
    }

    private void DisposeTray()
    {
        _tray?.Dispose();
        _tray = null;
    }

    // 会话仍有效时关闭所有打开的聊天（保存草稿等）
    private static async Task CloseChatsAsync(ZulipSession session)
    {
        foreach (var chat in session.Registry.All.ToList())
            await chat.CloseAsync();
        // 之前关闭的独立聊天窗口可能仍在保存草稿
        await session.Registry.WaitClosingAsync();
    }

    private void CloseSessionWindows()
    {
        foreach (var window in Desktop.Windows.OfType<ISessionWindow>().OfType<Window>().ToList())
            window.Close();
    }
}
