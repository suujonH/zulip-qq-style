using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>设置的分页。程序级的页在设置窗口中，账号的页在“Zulip 账号”页中，见 <see cref="SettingsViewModel.IsAccountScope"/>。</summary>
public enum SettingsPage
{
    // 程序级
    General,
    Files,
    Palette,

    /// <summary>Zulip 账号列表（添加、删除）。</summary>
    Accounts,
    Plugins,
    Developer,
    About,

    // 账号
    /// <summary>我（头像、资料、登录信息）。</summary>
    Account,

    /// <summary>保存在 Zulip 上的个人偏好：外观、发送消息的按键、24 小时制。</summary>
    Preferences,
    Notifications,
    EnhancedPush,
    GroupMessages,
    Privacy,
    AlertWords,
    Organization,

    /// <summary>服务器配置与信息。</summary>
    Server,
}

/// <summary>左侧导航的一项。</summary>
public sealed partial class SettingsNavItem(SettingsPage page, string title) : ObservableObject
{
    public SettingsPage Page { get; } = page;

    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>二级列表：当前页的分组标题。只有选中的页、且内容超过两屏时才有，由视图填写（见 SettingsSectionNav）。</summary>
    public ObservableCollection<SettingsNavSection> Sections { get; } = [];

    [ObservableProperty]
    public partial bool HasSections { get; private set; }

    /// <summary>替换二级列表；标题与现有的相同时不重建。</summary>
    public void SetSections(IReadOnlyList<string> titles)
    {
        if (!Sections.Select(s => s.Title).SequenceEqual(titles))
        {
            Sections.Clear();
            foreach (var title in titles)
                Sections.Add(new SettingsNavSection(title));
        }
        HasSections = Sections.Count > 0;
    }
}

/// <summary>二级列表的一项：页中的一个分组。<see cref="IsSelected"/> 跟随右侧的滚动位置。</summary>
public sealed partial class SettingsNavSection(string title) : ObservableObject
{
    public string Title { get; } = title;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>设置窗口中的一页。页面在第一次打开时创建，窗口关闭时统一 <see cref="Close"/>。</summary>
public abstract class SettingsPageViewModel(SettingsViewModel owner) : ViewModelBase
{
    protected SettingsViewModel Owner { get; } = owner;

    /// <summary>账号的设置页使用的会话（程序级设置的页面没有会话，不能使用）。</summary>
    protected ZulipSession Session => Owner.Session ?? throw new InvalidOperationException("程序级设置没有账号");

    /// <summary>切换到其它页时调用。</summary>
    public virtual void OnLeave()
    {
    }

    /// <summary>取消订阅等清理。</summary>
    public virtual void Close()
    {
    }

    /// <summary>页面占满设置窗口的右侧（不加外边距、不在窗口的滚动区域中），由页面自己布局与滚动。</summary>
    public virtual bool FillsWindow => false;

    /// <summary>
    /// 手动保存的页（点“保存”才提交）有未保存的修改：窗口在右侧下方显示保存 / 撤销提示，切换页、切换账号、关闭窗口前确认。
    /// 修改即提交的页总是 false。值变化时页面需要触发 PropertyChanged。
    /// </summary>
    public virtual bool HasUnsavedChanges => false;

    /// <summary>手动保存的页：提交修改。</summary>
    public virtual ICommand? SaveChangesCommand => null;

    /// <summary>手动保存的页：撤销修改（恢复为已保存的值）。</summary>
    public virtual ICommand? DiscardChangesCommand => null;

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
/// 设置的导航与当前页。设置窗口中是 ZulipQQ 自己的程序级设置（不属于任何账号，没有账号在线时也能打开）；
/// “Zulip 账号”页中每个在线账号一个，是该账号的设置（<see cref="IsAccountScope"/>）。
/// </summary>
public partial class SettingsViewModel : ViewModelBase
{
    private readonly Dictionary<SettingsPage, SettingsPageViewModel> _pages = [];
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private bool _confirmingLeave;

    /// <summary>程序级设置（设置窗口）。</summary>
    public SettingsViewModel() : this(null, null, false)
    {
    }

    /// <summary>账号的设置（“Zulip 账号”页）。</summary>
    public SettingsViewModel(ZulipSession session, MainViewModel main) : this(session, main, true)
    {
    }

    private SettingsViewModel(ZulipSession? session, MainViewModel? main, bool accountScope)
    {
        Session = session;
        Main = main;
        IsAccountScope = accountScope;
        Local = session?.Settings.Local ?? LocalSettings.App;
        Local.Changed += OnLocalChanged;
        BuildNav();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = null;
        };
    }

    /// <summary>账号的设置所属的会话；程序级设置为 null。</summary>
    public ZulipSession? Session { get; }

    public MainViewModel? Main { get; }

    /// <summary>本机设置：账号的设置为该账号的，程序级设置只读写程序级的设置。</summary>
    public LocalSettings Local { get; }

    /// <summary>账号的设置（“Zulip 账号”页）；否则为程序级设置（设置窗口）。</summary>
    public bool IsAccountScope { get; }

    /// <summary>开发者模式的“显示设置来源”：窗口据此显示各设置项下方的 <see cref="Controls.SettingSource"/>。</summary>
    public bool ShowSettingSource => Local.ShowSettingSource;

    public ObservableCollection<SettingsNavItem> Nav { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ScrolledPage), nameof(FilledPage))]
    public partial SettingsPageViewModel? CurrentPage { get; private set; }

    /// <summary>当前页显示在窗口的滚动区域中（大多数页）；占满右侧的页为 null。</summary>
    public SettingsPageViewModel? ScrolledPage => CurrentPage is { FillsWindow: false } page ? page : null;

    /// <summary>当前页占满窗口的右侧（“Zulip 账号”）；其他页为 null。</summary>
    public SettingsPageViewModel? FilledPage => CurrentPage is { FillsWindow: true } page ? page : null;

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
    public Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false, string cancelText = "取消")
    {
        var tcs = new TaskCompletionSource<bool>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        }, cancelText);
        Dialogs.Add(dialog);
        return tcs.Task;
    }

    /// <summary>带复选框的确认框：取消时返回 null，确定时返回复选框是否勾选。</summary>
    public Task<bool?> ConfirmWithCheckAsync(string message, string checkText, string confirmText, bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool?>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed ? dialog.IsChecked : null);
        }) { CheckText = checkText };
        Dialogs.Add(dialog);
        return tcs.Task;
    }

    /// <summary>
    /// 离开当前页（切换页、切换账号、关闭窗口）前调用：有未保存的修改时询问，选择“放弃变更”时撤销修改。
    /// 返回 true 表示可以离开；正在询问时再次调用返回 false。
    /// </summary>
    public async Task<bool> ConfirmLeaveAsync()
    {
        if (CurrentPage is not { HasUnsavedChanges: true } page)
            return true;
        if (_confirmingLeave)
            return false;
        _confirmingLeave = true;
        try
        {
            if (!await ConfirmAsync("有未保存的内容。", "放弃变更", isDanger: true, cancelText: "留在本页"))
                return false;
        }
        finally
        {
            _confirmingLeave = false;
        }
        page.DiscardChangesCommand?.Execute(null);
        return true;
    }

    /// <summary>确认可以离开当前页后切换到 <paramref name="page"/>。</summary>
    public async Task NavigateAsync(SettingsPage page)
    {
        if (page == CurrentKind || await ConfirmLeaveAsync())
            Navigate(page);
    }

    // 允许连续点击：询问中再次点击由 ConfirmLeaveAsync 忽略
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectAsync(SettingsNavItem item) => NavigateAsync(item.Page);

    /// <summary>切换到某一页；该页不在导航中时切换到第一页。</summary>
    public void Navigate(SettingsPage page)
    {
        if (Nav.All(n => n.Page != page))
            page = Nav[0].Page;
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
        Local.Changed -= OnLocalChanged;
        _toastTimer.Stop();
        foreach (var page in _pages.Values)
            page.Close();
        _pages.Clear();
    }

    private void OnLocalChanged(object? sender, string key)
    {
        if (key == LocalSettings.ShowSettingSourceKey)
            OnPropertyChanged(nameof(ShowSettingSource));
    }

    private SettingsPageViewModel Create(SettingsPage page) => page switch
    {
        SettingsPage.General => new GeneralPageViewModel(this),
        SettingsPage.Notifications => new NotificationsPageViewModel(this),
        SettingsPage.EnhancedPush => new EnhancedPushPageViewModel(this),
        SettingsPage.GroupMessages => new GroupMessagesPageViewModel(this),
        SettingsPage.Files => new FilesPageViewModel(this),
        SettingsPage.Palette => new PalettePageViewModel(this),
        SettingsPage.Accounts => new AccountsPageViewModel(this),
        SettingsPage.Account => new AccountPageViewModel(this),
        SettingsPage.Preferences => new PreferencesPageViewModel(this),
        SettingsPage.Server => new ServerPageViewModel(this),
        SettingsPage.Privacy => new PrivacyPageViewModel(this),
        SettingsPage.AlertWords => new AlertWordsPageViewModel(this),
        SettingsPage.Organization => new OrganizationPageViewModel(this),
        SettingsPage.Plugins => new PluginsPageViewModel(this),
        SettingsPage.Developer => new DeveloperPageViewModel(this),
        _ => new AboutPageViewModel(this),
    };

    private void BuildNav()
    {
        Nav.Clear();
        if (IsAccountScope)
        {
            Nav.Add(new SettingsNavItem(SettingsPage.Server, "服务器"));
            Nav.Add(new SettingsNavItem(SettingsPage.Account, "我"));
            Nav.Add(new SettingsNavItem(SettingsPage.Preferences, "偏好设置"));
            Nav.Add(new SettingsNavItem(SettingsPage.Notifications, "消息通知"));
            Nav.Add(new SettingsNavItem(SettingsPage.EnhancedPush, "强化推送"));
            Nav.Add(new SettingsNavItem(SettingsPage.GroupMessages, "群消息设置"));
            Nav.Add(new SettingsNavItem(SettingsPage.Privacy, "隐私"));
            Nav.Add(new SettingsNavItem(SettingsPage.AlertWords, "提醒词"));
            // 所有人可以查看，组织管理员可以修改
            Nav.Add(new SettingsNavItem(SettingsPage.Organization, "组织管理"));
            return;
        }
        Nav.Add(new SettingsNavItem(SettingsPage.General, "通用"));
        Nav.Add(new SettingsNavItem(SettingsPage.Files, "聊天中的文件"));
        Nav.Add(new SettingsNavItem(SettingsPage.Palette, "超级调色盘"));
        Nav.Add(new SettingsNavItem(SettingsPage.Accounts, "Zulip 账号"));
        Nav.Add(new SettingsNavItem(SettingsPage.Plugins, "插件"));
        Nav.Add(new SettingsNavItem(SettingsPage.Developer, "开发者模式"));
        Nav.Add(new SettingsNavItem(SettingsPage.About, "关于"));
    }
}
