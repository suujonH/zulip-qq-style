using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
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
            LocalDb.Instance = LocalDb.Open(AppPaths.DatabasePath);
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
            await session.DisposeAsync();
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
        if (ZulipSession.Current is { } current)
            await current.DisposeAsync();
        Desktop.Shutdown();
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

        var main = new MainWindow { DataContext = new MainViewModel(session) };
        Desktop.MainWindow = main;
        main.Show();

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

    // 会话仍有效时关闭所有打开的聊天（保存草稿等）
    private static async Task CloseChatsAsync(ZulipSession session)
    {
        foreach (var chat in session.Registry.All.ToList())
            await chat.CloseAsync();
    }

    private void CloseSessionWindows()
    {
        foreach (var window in Desktop.Windows.OfType<ISessionWindow>().OfType<Window>().ToList())
            window.Close();
    }
}
