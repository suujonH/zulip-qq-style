using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Data;
using ZulipQQ.Plugins;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;
using ZulipQQ.Views;
using ZulipQQ.Zulip;

namespace ZulipQQ;

/// <summary>
/// 程序：登录窗、主窗口与托盘。多个账号同时在线，共用一个主窗口和一个托盘图标；
/// 账号列表见 <see cref="AccountHubViewModel"/>，主窗口显示其中展开（<see cref="AccountHubViewModel.Active"/>）的账号，
/// 没有在线的账号时主窗口为空（没有消息和联系人）。登录窗只在启动时没有可登录的账号时显示，引导添加第一个账号。
/// </summary>
public partial class App : Application
{
    private readonly AccountHubViewModel _hub = AccountHubViewModel.Instance;
    private LoginWindow? _loginWindow;
    private LoginWindow? _addWindow;
    private MainWindow? _main;
    private bool _exiting;

    // 退出后启动新进程（RestartAsync）
    private bool _restart;
    private int _ending;
    private TrayService? _tray;

    // 在后台登录账号（启动时、启用账号时）；退出程序时取消
    private readonly CancellationTokenSource _restoreCts = new();

    public static new App Current => (App)Application.Current!;

    /// <summary>正在退出账号或退出程序；各窗口的 Closing 处理器据此不再拦截关闭。</summary>
    public static bool IsEndingSession => Current._ending > 0;

    private IClassicDesktopStyleApplicationLifetime Desktop =>
        (IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!;

    private AccountRepository Accounts => new(LocalDb.Instance!);

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
            // 登录窗与主窗口会互相替换，由代码决定何时退出
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            // 所有菜单的一级项按名称配图标
            MenuIcons.Register();
            // 重启（例如移动数据目录后）：等旧进程退出、释放数据库后再继续
            WaitForPreviousInstance(desktop.Args);
            // 设置中更改了数据目录：先在打开数据库之前搬迁，期间只显示进度窗口
            if (DataLocation.PendingMove is { } target)
                _ = StartAfterMoveAsync(target);
            else
                Start();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAfterMoveAsync(string target)
    {
        await DataMoveWindow.RunAsync(target);
        Start();
    }

    /// <summary>重启时新进程的参数：<c>--wait-pid 旧进程 id</c>。</summary>
    private const string WaitPidArg = "--wait-pid";

    private static void WaitForPreviousInstance(string[]? args)
    {
        var index = Array.IndexOf(args ?? [], WaitPidArg);
        if (index < 0 || index + 1 >= args!.Length || !int.TryParse(args[index + 1], out var pid))
            return;
        try
        {
            using var previous = System.Diagnostics.Process.GetProcessById(pid);
            if (!previous.WaitForExit(TimeSpan.FromSeconds(30)))
                Log.Warn($"等待旧进程 {pid} 退出超时");
        }
        catch (ArgumentException)
        {
            // 旧进程已经退出
        }
    }

    // 打开数据库、加载插件，然后进入主窗口或登录窗
    private void Start()
    {
        LocalDb.Instance = LocalDb.Open(AppPaths.DatabasePath);
        ThemeService.Initialize(LocalDb.Instance);
#if DEBUG
        RequestLog.Initialize(LocalDb.Instance);
#endif
        ZulipClient.UserAgent = ZulipClient.UserAgentFor(LocalSettings.App.ClientName);
        // 插件随程序启动（登录之前）加载
        PluginHost.Instance.LoadAll(new PluginStateRepository(LocalDb.Instance), new SettingRepository(LocalDb.Instance, null));
        // 插件菜单的键盘快捷键：在 ZulipQQ 的任意窗口中按下、且没有被焦点控件处理（例如输入框、快捷键录制框）时执行
        InputElement.KeyDownEvent.AddClassHandler<Window>((_, e) =>
        {
            if (PluginShortcuts.KeyGestureOf(e.Key, e.KeyModifiers) is { } gesture && PluginHost.Instance.InvokeKeyShortcut(gesture))
                e.Handled = true;
        });
        // 字号、托盘图标是程序级设置，没有账号在线时也能在设置中修改
        LocalSettings.App.Changed += OnAppSettingChanged;

        // 有可登录的账号（列表中启用且保存了 key）时直接打开主窗口，在后台登录它们；
        // 否则显示登录窗，预填列表中的第一个（没有时为最近登录的账号），也可以“稍后登录”先进入主窗口
        var accounts = Accounts;
        if (accounts.GetStartupAccounts().Count > 0)
            EnterMain();
        else
            ShowLogin(accounts.GetListed().FirstOrDefault() ?? accounts.GetAll().FirstOrDefault());
    }

    /// <summary>
    /// 退出一个账号（移出账号列表，下次启动不再登录）：关闭它的窗口、结束会话。最后一个在线的账号退出后主窗口为空。
    /// 保留 key 以便直接登录。<paramref name="deleteData"/> 为 true 时会话结束后删除该账号及其所有本地数据。
    /// </summary>
    public async Task LogoutAsync(ZulipSession session, bool confirm, bool deleteData = false)
    {
        if (_exiting || _hub.Find(session) is not { IsEnding: false, Main: { } main } entry)
            return;
        if (confirm && !await main.ConfirmAsync(_hub.IsMulti ? $"确定要退出账号“{entry.DisplayName}”吗？" : "确定要退出当前账号吗？"))
            return;
        if (entry.IsEnding || entry.Session != session)
            return;

        var accounts = Accounts;
        accounts.RemoveFromList(session.Account.Id);
        await EndEntryAsync(entry);
        _hub.Remove(entry);
        if (deleteData)
            accounts.DeleteWithData(session.Account.Id);
        UpdateTray();
    }

    /// <summary>
    /// 删除账号（设置的“Zulip 账号”）：清除保存的 key（删除登录状态）并退出、移出列表；服务器地址与邮箱保留在登录窗的历史账号中，
    /// 下次登录时关联原来的本地数据。<paramref name="deleteData"/> 为 true 时同时删除该账号及其所有本地数据。
    /// </summary>
    public async Task DeleteAccountAsync(AccountEntryViewModel entry, bool deleteData)
    {
        var accounts = Accounts;
        accounts.ClearKey(entry.AccountId);
        if (entry.Session is { } session)
        {
            await LogoutAsync(session, confirm: false, deleteData: deleteData);
            return;
        }
        accounts.RemoveFromList(entry.AccountId);
        _hub.Remove(entry);
        if (deleteData)
            accounts.DeleteWithData(entry.AccountId);
    }

    /// <summary>登录未启用的账号：启用它（打开“启动”）并在后台登录。</summary>
    public void LoginAccount(AccountEntryViewModel entry)
    {
        if (entry.IsDisabled)
            _ = SetLoginOnStartupAsync(entry, true);
    }

    /// <summary>
    /// 启用 / 停用账号（“启动”开关），立即生效：启用时在后台用保存的 key 登录（没有 key 时打开“添加账号”登录窗，登录成功后才启用）；
    /// 停用时退出该账号（保留 key，留在账号列表中显示“未启用”），正在进行的后台登录作废。
    /// </summary>
    public async Task SetLoginOnStartupAsync(AccountEntryViewModel entry, bool enabled)
    {
        if (_exiting || entry.IsEnding || !_hub.Entries.Contains(entry))
        {
            entry.NotifyLoginOnStartupChanged();
            return;
        }
        if (enabled)
        {
            if (!entry.Account.HasKey)
            {
                entry.NotifyLoginOnStartupChanged();
                ShowAddAccount(entry.Account);
                return;
            }
            Accounts.SetLoginOnStartup(entry.AccountId, true);
            entry.NotifyLoginOnStartupChanged();
            if (entry.IsDisabled)
                _ = RestoreAsync(entry, entry.MarkConnecting(), _restoreCts.Token);
            return;
        }
        Accounts.SetLoginOnStartup(entry.AccountId, false);
        entry.NotifyLoginOnStartupChanged();
        if (entry.IsOnline)
        {
            await EndEntryAsync(entry);
            entry.IsEnding = false;
            UpdateTray();
        }
        entry.MarkDisabled();
    }

    /// <summary>退出程序：结束所有会话（不修改自动登录和账号列表）后关闭。</summary>
    public async Task ExitAsync()
    {
        if (_exiting)
            return;
        _exiting = true;
        _ending++;
        _restoreCts.Cancel();
        var entries = _hub.Online;
        foreach (var entry in entries)
        {
            entry.IsEnding = true;
            await CloseChatsAsync(entry.Session!);
        }
        CloseSessionWindows(null, all: true);
        DisposeTray();
        foreach (var entry in entries)
        {
            await EndPluginSessionAsync(entry.Session!);
            await entry.Session!.DisposeAsync();
        }
        PluginHost.Instance.Shutdown();
        if (_restart)
            StartNewInstance();
        Desktop.Shutdown();
    }

    /// <summary>
    /// 重启程序（例如移动数据目录后）：与退出相同地结束所有会话，然后启动新进程。
    /// 在线的账号仍在账号列表中，新进程启动时自动登录它们。
    /// </summary>
    public Task RestartAsync()
    {
        _restart = true;
        return ExitAsync();
    }

    // 新进程等本进程退出（释放数据库）后再启动，见 WaitForPreviousInstance
    private static void StartNewInstance()
    {
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
            info.ArgumentList.Add(WaitPidArg);
            info.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("重启失败", ex);
        }
    }

    /// <summary>
    /// 用户关闭主面板（标题栏 ×、Alt+F4 等）：设置为最小化到托盘且托盘图标可见时隐藏主面板，否则退出程序。
    /// </summary>
    public void RequestCloseMain()
    {
        if (_tray is { IsVisible: true } && LocalSettings.App.EffectiveCloseAction == CloseAction.MinimizeToTray)
            _main?.Hide();
        else
            _ = ExitAsync();
    }

    /// <summary>
    /// 点击导航或账号列表中的账号：在线时主窗口切换到该账号；登录失败的账号打开“添加账号”登录窗重新登录；
    /// 未启用的账号启用并登录。
    /// </summary>
    public void SelectAccount(AccountEntryViewModel entry)
    {
        if (entry.IsFailed)
        {
            ShowAddAccount(entry.Account, entry.ErrorText);
            return;
        }
        if (entry.IsDisabled)
        {
            LoginAccount(entry);
            return;
        }
        if (!entry.IsOnline || entry.IsEnding || _hub.Active == entry || _main is null)
            return;
        var old = _hub.Active;
        _hub.SetActive(entry);
        _main.DataContext = entry.Main;
        ApplySessionTheme(entry.Session!);
        UpdateTray();
        // 切走的账号：关闭右侧的聊天，避免看不见的聊天把新消息标记为已读
        if (old?.Main is { } oldMain)
            _ = oldMain.DeactivateAsync();
    }

    /// <summary>打开“添加账号”登录窗（已打开时激活并预填）。登录成功后账号加到列表末尾并切换到它。</summary>
    public void ShowAddAccount(AccountRow? prefill = null, string? error = null)
    {
        if (_addWindow is { } existing)
        {
            if (existing.DataContext is LoginViewModel { IsBusy: false } busyFree && prefill is not null)
                busyFree.Prefill(prefill, error);
            WindowPlacement.BringToFront(existing);
            return;
        }
        var loggedIn = _hub.Entries.Where(e => e.IsOnline || e.IsConnecting).Select(e => e.AccountId).ToHashSet();
        var vm = new LoginViewModel(Accounts, AddAccountAsync, prefill, error, loggedIn);
        var window = new LoginWindow { DataContext = vm, Title = "添加账号" };
        window.Closed += (_, _) =>
        {
            vm.Cancel();
            if (_addWindow == window)
                _addWindow = null;
        };
        _addWindow = window;
        WindowPlacement.CenterOn(window, _main);
        window.Show();
    }

    /// <summary>
    /// 删除账号：退出该账号并移出列表（登录窗中保留登录记录）。在线时与“退出账号”相同（不再确认）；
    /// 正在登录或登录失败的账号直接从导航中移除，后台登录的结果会被丢弃。
    /// </summary>
    public async Task RemoveAccountAsync(AccountEntryViewModel entry)
    {
        if (entry.Session is { } session)
        {
            await LogoutAsync(session, confirm: false);
            return;
        }
        Accounts.RemoveFromList(entry.AccountId);
        _hub.Remove(entry);
    }

    /// <summary>
    /// 显示并激活主面板（可能隐藏在托盘中）。<paramref name="main"/> 为附属窗口所属账号的主面板时先切换到该账号。
    /// 附属窗口“定位到聊天”、转发等需要主面板时调用。
    /// </summary>
    public void ShowMainWindow(MainViewModel? main = null)
    {
        if (main is not null && _hub.Find(main) is { } entry)
            SelectAccount(entry);
        if (_main is { } window)
            ShowMain(window);
    }

    // 启动时没有可登录的账号：登录窗登录第一个账号，或“稍后登录”先进入空的主窗口
    private void ShowLogin(AccountRow? prefill)
    {
        var vm = new LoginViewModel(Accounts, CompleteLoginAsync, prefill, later: () =>
        {
            EnterMain();
            _loginWindow?.Close();
        });
        var window = new LoginWindow { DataContext = vm };
        window.Closed += (_, _) =>
        {
            vm.Cancel();
            if (_loginWindow == window)
                _loginWindow = null;
            // 用户关闭登录窗时退出程序；登录成功或“稍后登录”时先打开了主窗口，不退出
            if (_main is null)
                Desktop.Shutdown();
        };
        _loginWindow = window;
        Desktop.MainWindow = window;
        window.Show();
    }

    // 登录窗登录的账号：启用该账号，打开主窗口并显示它（列表中未启用的账号也加到导航中）
    private async Task CompleteLoginAsync(AccountRow account, CancellationToken ct)
    {
        var session = await ZulipSession.CreateAsync(account, ct);
        if (ct.IsCancellationRequested)
        {
            await session.DisposeAsync();
            ct.ThrowIfCancellationRequested();
        }

        var entry = new AccountEntryViewModel(session.Account, AccountEntryState.Online);
        _hub.Add(entry);
        Accounts.SetLoginOnStartup(account.Id, true);
        StartSession(entry, session);
        EnterMain();
        SelectAccount(entry);
        _loginWindow?.Close();
    }

    // 打开主窗口（没有显示的账号时为空）和托盘，在后台登录列表中启用的账号
    private void EnterMain()
    {
        if (_main is not null)
            return;
        ApplyAppTheme();
        _main = new MainWindow();
        Desktop.MainWindow = _main;
        _main.Show();
        CreateTray();
        RestoreAccounts();
    }

    // “添加账号”登录窗：账号已登录（或正在登录）时报错；登录失败过、未启用的账号替换导航中原来的条目，并启用它
    private async Task AddAccountAsync(AccountRow account, CancellationToken ct)
    {
        if (_hub.Find(account.Id) is { IsOnline: true } or { IsConnecting: true })
            throw new UserFacingException("该账号已登录");
        var session = await ZulipSession.CreateAsync(account, ct);
        if (ct.IsCancellationRequested || _exiting || _main is null || _hub.Find(account.Id) is { IsOnline: true } or { IsConnecting: true })
        {
            await session.DisposeAsync();
            ct.ThrowIfCancellationRequested();
            throw new UserFacingException("该账号已登录");
        }

        Accounts.SetLoginOnStartup(account.Id, true);
        var entry = _hub.Find(account.Id);
        if (entry is null)
        {
            entry = new AccountEntryViewModel(session.Account, AccountEntryState.Online);
            _hub.Add(entry);
        }
        entry.NotifyLoginOnStartupChanged();
        StartSession(entry, session);
        SelectAccount(entry);
        _addWindow?.Close();
        ShowMain(_main);
    }

    // 列表中还不在导航中的账号：启用且保存了 key 的先显示“正在登录”，用保存的 key 在后台登录，失败时显示错误；
    // 其他的显示“未启用”，点击后启用并登录
    private void RestoreAccounts()
    {
        var accounts = Accounts;
        foreach (var account in accounts.GetListed().Where(a => _hub.Find(a.Id) is null))
        {
            if (!account.HasKey || !accounts.IsLoginOnStartup(account.Id))
            {
                _hub.Add(new AccountEntryViewModel(account, AccountEntryState.Disabled));
                continue;
            }
            var entry = new AccountEntryViewModel(account, AccountEntryState.Disabled);
            _hub.Add(entry);
            _ = RestoreAsync(entry, entry.MarkConnecting(), _restoreCts.Token);
        }
    }

    // attempt：开始登录时的序号（MarkConnecting 的返回值），期间重新开始或停用了登录时丢弃结果
    private async Task RestoreAsync(AccountEntryViewModel entry, int attempt, CancellationToken ct)
    {
        ZulipSession session;
        try
        {
            session = await ZulipSession.CreateAsync(entry.Account, ct);
        }
        catch (Exception ex)
        {
            if (ct.IsCancellationRequested || entry.LoginAttempt != attempt)
                return;
            Log.Warn($"后台登录失败：{entry.Account.Email}", ex);
            entry.MarkFailed(LoginViewModel.DescribeError(ex));
            return;
        }
        // 期间退出了程序，用户删除或停用了这个账号，或重新开始了登录
        if (ct.IsCancellationRequested || _exiting || !_hub.Entries.Contains(entry) || entry.LoginAttempt != attempt
            || !entry.IsConnecting)
        {
            await session.DisposeAsync();
            return;
        }
        StartSession(entry, session);
        UpdateTray();
    }

    // 登录成功：账号加入列表，创建主面板，连接通知、外观、托盘等，开始事件循环。主窗口为空时显示该账号
    private void StartSession(AccountEntryViewModel entry, ZulipSession session)
    {
        Accounts.AddToList(session.Account.Id);
        var mainVm = new MainViewModel(session);
        entry.Attach(session, mainVm);

        session.Settings.Changed += (_, name) =>
        {
            if (name == "color_scheme" && _hub.Active == entry)
                ThemeService.ApplyColorScheme(session.Settings.Zulip.ColorScheme);
        };
        session.Resynced += (_, _) =>
        {
            if (_hub.Active == entry)
                ApplySessionTheme(session);
        };
        session.Notifications.Requested += (_, request) => ShowNotification(entry, request);
        // 没有需要处理的未读时停止闪烁；托盘提醒列表随未读与消息提醒更新
        session.Unread.ConversationChanged += (_, _) =>
        {
            StopFlashingIfRead();
            TrayMessageListWindow.Refresh();
        };
        session.SystemNotices.Changed += (_, _) => TrayMessageListWindow.Refresh();
        session.Reminders.Changed += (_, _) => TrayMessageListWindow.Refresh();
        // 其他窗口要在主面板中显示该账号的内容（打开聊天、系统通知等）：先切换到该账号
        mainVm.ShowRequested += (_, _) => SelectAccount(entry);
        mainVm.MainWindowRequested += (_, _) => ShowMainWindow(mainVm);
        session.ConnectionChanged += (_, _) =>
        {
            if (session.Connection == ConnectionState.AuthFailed)
                _ = OnAuthFailedAsync(entry, session);
        };
        session.Start();
        PluginHost.Instance.OnSessionStarted(session);
        if (_hub.Active is null && _main is not null)
            SelectAccount(entry);
    }

    // API key 失效：结束这个会话，在导航中显示“登录已失效”，点击后重新登录
    private async Task OnAuthFailedAsync(AccountEntryViewModel entry, ZulipSession session)
    {
        if (entry.IsEnding || entry.Session != session)
            return;
        await EndEntryAsync(entry);
        entry.IsEnding = false;
        entry.MarkFailed("登录已失效，请重新登录");
        UpdateTray();
    }

    // 结束一个在线账号的会话：主窗口正在显示它时先切换到其他在线的账号（没有时主窗口为空）
    private async Task EndEntryAsync(AccountEntryViewModel entry)
    {
        entry.IsEnding = true;
        if (_hub.Active == entry)
            SelectFirstOnline();
        await EndSessionAsync(entry);
    }

    // 结束一个账号的会话：关闭聊天（保存草稿）和它的窗口，通知插件，销毁会话。主窗口不关闭
    private async Task EndSessionAsync(AccountEntryViewModel entry)
    {
        var session = entry.Session!;
        entry.IsEnding = true;
        _ending++;
        try
        {
            await CloseChatsAsync(session);
            CloseSessionWindows(session, all: false);
            await EndPluginSessionAsync(session);
            await session.DisposeAsync();
        }
        finally
        {
            _ending--;
        }
    }

    // 主窗口切换到第一个在线的账号；没有时主窗口为空
    private void SelectFirstOnline()
    {
        if (_hub.Online.FirstOrDefault() is { } next)
        {
            SelectAccount(next);
            return;
        }
        _hub.SetActive(null);
        if (_main is not null)
            _main.DataContext = null;
        ApplyAppTheme();
        UpdateTray();
    }

    // 没有显示的账号时（登录窗、空的主窗口）：默认外观与程序级的字号
    private static void ApplyAppTheme()
    {
        ThemeService.SetPreviewDark(false);
        ThemeService.ApplyColorScheme(1);
        ThemeService.ApplyFontSize(LocalSettings.App.FontSize);
    }

    // 通知插件该账号结束（会话仍有效），最多等待 2 秒
    private static Task EndPluginSessionAsync(ZulipSession session) =>
        Task.WhenAny(PluginHost.Instance.OnSessionEnded(session), Task.Delay(2000));

    // 外观（Zulip 的 color_scheme，跟随主窗口显示的账号）与本地字号（程序级）
    private static void ApplySessionTheme(ZulipSession session)
    {
        ThemeService.ApplyColorScheme(session.Settings.Zulip.ColorScheme);
        ThemeService.ApplyFontSize(session.Settings.Local.FontSize);
    }

    // 主窗口在前台（焦点在设置等其他窗口时不算）且显示着该账号时不提醒；否则闪烁任务栏和托盘图标，需要时弹出通知，
    // 点击后切换到该账号、把主窗口带到前台，打开会话并定位到触发通知的消息
    private void ShowNotification(AccountEntryViewModel entry, NotificationRequest request)
    {
        if (_main is not { } main || entry.Main is not { } vm)
            return;
        if (TaskbarFlash.IsForeground(main) && !EdgeHideController.IsHiddenAtEdge(main) && _hub.Active == entry)
            return;
        TaskbarFlash.Start(main);
        _tray?.StartFlashing();
        // 托盘提醒列表打开时不弹出通知（会挡住列表），新消息直接出现在列表中
        if (!request.Popup || TrayMessageListWindow.IsOpen)
            return;
        var session = entry.Session!;
        NotificationPopupWindow.Show(session, _hub.IsMulti ? entry.DisplayName : null, request, () =>
        {
            if (entry.Session != session || _main is null)
                return;
            // 消息提醒打开提醒窗口的“已提醒”
            if (request.IsReminder)
            {
                RemindersWindow.ShowSingle(_main, vm, ReminderTab.Delivered);
                return;
            }
            SelectAccount(entry);
            ShowMain(_main);
            _ = vm.OpenChatAsync(request.Key, request.MessageId);
        });
    }

    private static void ShowMain(Window main)
    {
        main.Show();
        if (main.WindowState == WindowState.Minimized)
            main.WindowState = WindowState.Normal;
        main.Activate();
    }

    // 托盘图标：是否显示由本地设置（程序级）决定；“退出账号”退出主窗口正在显示的账号
    private void CreateTray()
    {
        if (_tray is not null || _main is not { } main)
            return;
        var tray = new TrayService(TrayToolTip(), () => ShowMainWindow(), () =>
            {
                if (_hub.Active?.Session is not { } active)
                    return;
                // 确认框显示在主面板中
                ShowMainWindow();
                _ = LogoutAsync(active, confirm: true);
            }, () => _ = ExitAsync())
        {
            IsVisible = LocalSettings.App.ShowTrayIcon,
        };
        _tray = tray;
        main.Activated += (_, _) => tray.StopFlashing();
        tray.Hovered += (_, _) => TrayMessageListWindow.Show(BuildTrayItems, tray.GetIconRect());
        tray.FlashingStopped += (_, _) => TrayMessageListWindow.CloseCurrent();
    }

    // 托盘提醒列表：所有在线账号的未读会话与消息提醒；点击后与通知弹窗相同（切换账号并打开会话，或打开消息提醒窗口）
    private IReadOnlyList<TrayMessageItemViewModel> BuildTrayItems()
    {
        var now = DateTime.Now;
        var multi = _hub.IsMulti;
        return TrayMessageItemViewModel.Sort(_hub.Online
            .Where(e => e is { IsEnding: false, Session: not null, Main: not null })
            .SelectMany(entry =>
            {
                var session = entry.Session!;
                var vm = entry.Main!;
                return TrayMessageItemViewModel.Build(session, multi ? entry.DisplayName : null, now,
                    (key, focusMessageId) =>
                    {
                        if (entry.Session != session || _main is null)
                            return;
                        SelectAccount(entry);
                        ShowMain(_main);
                        _ = vm.OpenChatAsync(key, focusMessageId);
                    },
                    tab =>
                    {
                        if (entry.Session == session && _main is not null)
                            RemindersWindow.ShowSingle(_main, vm, tab);
                    });
            }));
    }

    // 程序级设置：字号、托盘图标
    private void OnAppSettingChanged(object? sender, string key)
    {
        if (key == "font_size")
            ThemeService.ApplyFontSize(LocalSettings.App.FontSize);
        else if (key == LocalSettings.ShowTrayIconKey)
            OnShowTrayIconChanged();
        else if (key == LocalSettings.ClientNameKey)
            ZulipClient.UserAgent = ZulipClient.UserAgentFor(LocalSettings.App.ClientName);
    }

    private void OnShowTrayIconChanged()
    {
        if (_tray is not { } tray)
            return;
        tray.IsVisible = LocalSettings.App.ShowTrayIcon;
        // 主面板隐藏在托盘中时去掉托盘图标，否则无法再打开主面板
        if (!tray.IsVisible && _main is { IsVisible: false } main)
            ShowMain(main);
    }

    // 所有在线账号都没有需要处理的未读时停止闪烁：允许提醒的会话的未读，以及免打扰会话中提醒过（@我、@全体、提醒词）的未读消息
    private void StopFlashingIfRead()
    {
        if (_tray is not { IsFlashing: true } tray)
            return;
        if (_hub.Online.All(e => e.Session is not { } s
                || (s.Unread.TotalForBadge(s.Conversations.GetEffectiveLevel) == 0 && !s.Notifications.HasUnreadMutedAlerts())))
            tray.StopFlashing();
    }

    private string TrayToolTip()
    {
        var names = _hub.Online.Select(e => e.DisplayName).ToList();
        return names.Count == 0 ? "ZulipQQ" : $"ZulipQQ - {string.Join("、", names)}";
    }

    private void UpdateTray()
    {
        _tray?.SetToolTip(TrayToolTip());
        TrayMessageListWindow.Refresh();
    }

    private void DisposeTray()
    {
        TrayMessageListWindow.CloseCurrent();
        _tray?.Dispose();
        _tray = null;
    }

    // 会话仍有效时关闭该账号所有打开的聊天（保存草稿等）
    private static async Task CloseChatsAsync(ZulipSession session)
    {
        foreach (var chat in session.Registry.All.ToList())
            await chat.CloseAsync();
        // 之前关闭的独立聊天窗口可能仍在保存草稿
        await session.Registry.WaitClosingAsync();
    }

    // 关闭属于该账号的窗口（主窗口除外）；all 为 true 时关闭所有会话窗口，包括没有绑定账号的
    private void CloseSessionWindows(ZulipSession? session, bool all)
    {
        foreach (var window in Desktop.Windows.OfType<ISessionWindow>().OfType<Window>().ToList())
        {
            if (window is MainWindow)
                continue;
            var owner = SessionWindow.Of(window);
            if (all || (owner is not null && owner == session))
            {
                // 收藏、文件窗口：先切换到其他在线账号
                if (!all && window is IAccountSwitchWindow switchable && switchable.SwitchAway(session!))
                    continue;
                window.Close();
            }
        }
    }
}
