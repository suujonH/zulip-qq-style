using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>设置窗口的分页。</summary>
public enum SettingsPage
{
    General,
    Notifications,
    GroupMessages,
    Files,
    Palette,
    Account,
    Privacy,
    AlertWords,
    Organization,
    About,
}

/// <summary>左侧导航的一项。</summary>
public sealed partial class SettingsNavItem(SettingsPage page, string title) : ObservableObject
{
    public SettingsPage Page { get; } = page;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>设置窗口中的一页。页面在第一次打开时创建，窗口关闭时统一 <see cref="Close"/>。</summary>
public abstract class SettingsPageViewModel(SettingsViewModel owner) : ViewModelBase
{
    protected SettingsViewModel Owner { get; } = owner;

    protected ZulipSession Session => Owner.Session;

    /// <summary>切换到其它页时调用。</summary>
    public virtual void OnLeave()
    {
    }

    /// <summary>取消订阅等清理。</summary>
    public virtual void Close()
    {
    }

    /// <summary>修改 Zulip 设置；失败时提示（SettingsStore 会恢复原值并触发 Changed）。</summary>
    protected async void SetZulip(string name, object value)
    {
        try
        {
            await Session.Settings.SetZulipAsync(name, value);
        }
        catch (Exception ex)
        {
            // async void 中漏出的异常会使程序崩溃：退出账号后请求被取消，此时静默结束
            if (Session.IsDisposed)
                return;
            Log.Warn($"修改设置失败：{name}", ex);
            Owner.ShowToast("设置失败");
        }
    }
}

/// <summary>
/// 设置窗口：左侧导航、右侧当前页。组织管理页只有组织管理员可见，权限变化时随之出现或消失。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly Dictionary<SettingsPage, SettingsPageViewModel> _pages = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    public SettingsViewModel(ZulipSession session, MainViewModel main)
    {
        Session = session;
        Main = main;
        BuildNav();
        Session.PermissionsChanged += OnPermissionsChanged;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = null;
        };
    }

    public ZulipSession Session { get; }

    public MainViewModel Main { get; }

    public ObservableCollection<SettingsNavItem> Nav { get; } = [];

    [ObservableProperty]
    public partial SettingsPageViewModel? CurrentPage { get; private set; }

    public SettingsPage? CurrentKind { get; private set; }

    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    public void ShowToast(string text)
    {
        ToastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>显示确认对话框，返回用户是否点击了确定。</summary>
    public Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        });
        Dialogs.Add(dialog);
        return tcs.Task;
    }

    [RelayCommand]
    private void Select(SettingsNavItem item) => Navigate(item.Page);

    /// <summary>切换到某一页；该页不可见（例如没有组织管理权限）时切换到通用页。</summary>
    public void Navigate(SettingsPage page)
    {
        if (Nav.All(n => n.Page != page))
            page = SettingsPage.General;
        foreach (var item in Nav)
            item.IsSelected = item.Page == page;
        if (!_pages.TryGetValue(page, out var vm))
        {
            vm = Create(page);
            _pages[page] = vm;
        }
        if (CurrentPage != vm)
            CurrentPage?.OnLeave();
        CurrentKind = page;
        CurrentPage = vm;
    }

    public void Close()
    {
        Session.PermissionsChanged -= OnPermissionsChanged;
        _toastTimer.Stop();
        foreach (var page in _pages.Values)
            page.Close();
        _pages.Clear();
    }

    private SettingsPageViewModel Create(SettingsPage page) => page switch
    {
        SettingsPage.General => new GeneralPageViewModel(this),
        SettingsPage.Notifications => new NotificationsPageViewModel(this),
        SettingsPage.GroupMessages => new GroupMessagesPageViewModel(this),
        SettingsPage.Files => new FilesPageViewModel(this),
        SettingsPage.Palette => new PalettePageViewModel(this),
        SettingsPage.Account => new AccountPageViewModel(this),
        SettingsPage.Privacy => new PrivacyPageViewModel(this),
        SettingsPage.AlertWords => new AlertWordsPageViewModel(this),
        SettingsPage.Organization => new OrganizationPageViewModel(this),
        _ => new AboutPageViewModel(this),
    };

    private void BuildNav()
    {
        Nav.Clear();
        Nav.Add(new SettingsNavItem(SettingsPage.General, "通用"));
        Nav.Add(new SettingsNavItem(SettingsPage.Notifications, "消息通知"));
        Nav.Add(new SettingsNavItem(SettingsPage.GroupMessages, "群消息设置"));
        Nav.Add(new SettingsNavItem(SettingsPage.Files, "聊天中的文件"));
        Nav.Add(new SettingsNavItem(SettingsPage.Palette, "超级调色盘"));
        Nav.Add(new SettingsNavItem(SettingsPage.Account, "Zulip 账号"));
        Nav.Add(new SettingsNavItem(SettingsPage.Privacy, "隐私"));
        Nav.Add(new SettingsNavItem(SettingsPage.AlertWords, "提醒词"));
        if (Session.Permissions.IsOrgAdmin)
            Nav.Add(new SettingsNavItem(SettingsPage.Organization, "组织管理"));
        Nav.Add(new SettingsNavItem(SettingsPage.About, "关于"));
    }

    // 失去组织管理员身份时移除组织管理页（正在显示时切回通用页），获得时加入
    private void OnPermissionsChanged(object? sender, EventArgs e)
    {
        var hasOrg = Nav.Any(n => n.Page == SettingsPage.Organization);
        if (hasOrg == Session.Permissions.IsOrgAdmin)
            return;
        BuildNav();
        if (!Session.Permissions.IsOrgAdmin && _pages.Remove(SettingsPage.Organization, out var org))
            org.Close();
        Navigate(CurrentKind ?? SettingsPage.General);
    }
}
