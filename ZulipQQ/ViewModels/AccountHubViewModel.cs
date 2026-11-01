using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Data;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public enum AccountEntryState
{
    /// <summary>启动时在后台登录中。</summary>
    Connecting,

    Online,

    /// <summary>登录失败或登录已失效：点击后打开登录窗重新登录。</summary>
    Failed,

    /// <summary>未启用（关闭了“启动”）的账号：不登录，点击后启用并登录。</summary>
    Disabled,
}

/// <summary>
/// 主窗口左侧导航中的一个账号。在线时持有该账号的会话和主面板（<see cref="Main"/>），
/// 展开（<see cref="IsActive"/>）时主窗口显示它的内容，收起时头像上显示未读角标。
/// </summary>
public sealed partial class AccountEntryViewModel : ViewModelBase
{
    public AccountEntryViewModel(AccountRow account, AccountEntryState state, string? error = null)
    {
        Account = account;
        State = state;
        ErrorText = error;
    }

    public AccountRow Account { get; private set; }

    public long AccountId => Account.Id;

    public ZulipSession? Session { get; private set; }

    public MainViewModel? Main { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOnline), nameof(IsConnecting), nameof(IsFailed), nameof(IsDisabled), nameof(StatusText),
        nameof(ToolTip))]
    public partial AccountEntryState State { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(ToolTip))]
    public partial string? ErrorText { get; private set; }

    /// <summary>主窗口正在显示该账号（导航展开）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBadge), nameof(ShowsDot))]
    public partial bool IsActive { get; set; }

    /// <summary>正在退出该账号（避免重复退出）。</summary>
    public bool IsEnding { get; set; }

    /// <summary>第几次开始后台登录：重新开始后，之前未完成的登录结果作废。</summary>
    public int LoginAttempt { get; private set; }

    public bool IsOnline => State == AccountEntryState.Online;

    public bool IsConnecting => State == AccountEntryState.Connecting;

    public bool IsFailed => State == AccountEntryState.Failed;

    public bool IsDisabled => State == AccountEntryState.Disabled;

    /// <summary>“启动”（启用）：切换立即生效，开启时登录该账号，关闭时退出（留在账号列表中）；启动程序时只登录开启的账号。</summary>
    public bool LoginOnStartup
    {
        get => new AccountRepository(LocalDb.Instance!).IsLoginOnStartup(AccountId);
        set
        {
            if (value != LoginOnStartup)
                _ = App.Current.SetLoginOnStartupAsync(this, value);
        }
    }

    /// <summary>“启动”的值已修改（或修改被取消，开关需要恢复）。</summary>
    public void NotifyLoginOnStartupChanged() => OnPropertyChanged(nameof(LoginOnStartup));

    public string DisplayName =>
        Session is { } s ? s.Users.Get(s.SelfId)?.FullName ?? Account.FullName : Account.FullName is { Length: > 0 } n ? n : Account.Email;

    public string? AvatarUrl => Session is { } s ? s.Users.Get(s.SelfId)?.AvatarUrl ?? Account.AvatarUrl : Account.AvatarUrl;

    /// <summary>服务器地址（去掉 https://），用于区分不同组织的同名账号。</summary>
    public string RealmText => Account.RealmUrl.Replace("https://", "", StringComparison.OrdinalIgnoreCase);

    public string StatusText => State switch
    {
        AccountEntryState.Online => "在线",
        AccountEntryState.Connecting => "正在登录…",
        AccountEntryState.Disabled => "未启用",
        _ => ErrorText ?? "登录失败",
    };

    public string ToolTip => State switch
    {
        AccountEntryState.Online or AccountEntryState.Connecting => $"{DisplayName}\n{Account.Email}\n{RealmText}",
        AccountEntryState.Disabled => $"{DisplayName}\n{Account.Email}\n{RealmText}\n{StatusText}",
        _ => $"{DisplayName}\n{Account.Email}\n{RealmText}\n{StatusText}，点击重新登录",
    };

    /// <summary>未读总数（与该账号“消息”导航上的数字相同）。</summary>
    public string UnreadText => Main?.Messages.TotalUnreadText ?? "";

    /// <summary>收起时头像上显示未读数。</summary>
    public bool ShowsBadge => !IsActive && UnreadText.Length > 0;

    /// <summary>收起时没有未读数、但有系统通知或提醒未读时显示红点。</summary>
    public bool ShowsDot => !IsActive && UnreadText.Length == 0 && Main is { HasContactsDot: true };

    /// <summary>登录成功：关联会话和主面板。</summary>
    public void Attach(ZulipSession session, MainViewModel main)
    {
        Detach();
        Session = session;
        Main = main;
        Account = session.Account;
        ErrorText = null;
        State = AccountEntryState.Online;
        main.PropertyChanged += OnMainPropertyChanged;
        main.Messages.PropertyChanged += OnMessagesPropertyChanged;
        session.Users.UserChanged += OnUserChanged;
        session.Users.Reset += OnUsersReset;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>会话结束（登录失效等）：保留在导航中，显示错误，点击后重新登录。</summary>
    public void MarkFailed(string error)
    {
        Detach();
        ErrorText = error;
        State = AccountEntryState.Failed;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>开始在后台登录（启用了账号，或未启用的账号被点击）；返回这次登录的序号。</summary>
    public int MarkConnecting()
    {
        ErrorText = null;
        State = AccountEntryState.Connecting;
        return ++LoginAttempt;
    }

    /// <summary>未启用：会话已结束（或不再登录），留在导航中，点击后启用并登录。</summary>
    public void MarkDisabled()
    {
        Detach();
        ErrorText = null;
        LoginAttempt++;
        State = AccountEntryState.Disabled;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>取消对会话和主面板的订阅。</summary>
    public void Detach()
    {
        if (Main is { } main)
        {
            main.PropertyChanged -= OnMainPropertyChanged;
            main.Messages.PropertyChanged -= OnMessagesPropertyChanged;
        }
        if (Session is { } session)
        {
            session.Users.UserChanged -= OnUserChanged;
            session.Users.Reset -= OnUsersReset;
        }
        Main = null;
        Session = null;
    }

    [RelayCommand]
    private void Select() => App.Current.SelectAccount(this);

    private void OnMainPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.HasContactsDot))
            OnPropertyChanged(nameof(ShowsDot));
    }

    private void OnMessagesPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MessagesPageViewModel.TotalUnreadText))
            return;
        OnPropertyChanged(nameof(UnreadText));
        OnPropertyChanged(nameof(ShowsBadge));
        OnPropertyChanged(nameof(ShowsDot));
    }

    private void OnUserChanged(object? sender, long id)
    {
        if (Session is { } s && id == s.SelfId)
            OnUsersReset(sender, EventArgs.Empty);
    }

    private void OnUsersReset(object? sender, EventArgs e)
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(AvatarUrl));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// 所有账号（主窗口左侧导航、设置中的账号列表共用）。顺序与数据库中的列表顺序一致，第一个是主账号。
/// 登录、切换、退出由 <see cref="App"/> 完成，这里只保存状态。只在 UI 线程上使用。
/// </summary>
public sealed partial class AccountHubViewModel : ViewModelBase
{
    private AccountHubViewModel()
    {
        Entries.CollectionChanged += OnEntriesChanged;
    }

    public static AccountHubViewModel Instance { get; } = new();

    public ObservableCollection<AccountEntryViewModel> Entries { get; } = [];

    /// <summary>主窗口正在显示的账号；没有在线的账号时为 null（主窗口为空）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsAccountButtons))]
    public partial AccountEntryViewModel? Active { get; private set; }

    /// <summary>有多个启用的账号（未启用的不在主窗口中显示）：导航中显示账号头像，点击展开。</summary>
    public bool IsMulti => Entries.Count(e => !e.IsDisabled) > 1;

    /// <summary>导航中显示账号头像：多个账号时，或主窗口为空时（正在登录、登录失败的账号）。</summary>
    public bool ShowsAccountButtons => IsMulti || Active is null;

    public AccountEntryViewModel? Find(long accountId) => Entries.FirstOrDefault(e => e.AccountId == accountId);

    public AccountEntryViewModel? Find(ZulipSession session) => Entries.FirstOrDefault(e => e.Session == session);

    public AccountEntryViewModel? Find(MainViewModel main) => Entries.FirstOrDefault(e => e.Main == main);

    /// <summary>在线的账号。</summary>
    public AccountEntryViewModel[] Online => [.. Entries.Where(e => e.IsOnline && !e.IsEnding)];

    /// <summary>在线的账号（可绑定，顺序同 <see cref="Entries"/>），用于收藏、文件窗口中切换账号。</summary>
    public ObservableCollection<AccountEntryViewModel> OnlineEntries { get; } = [];

    /// <summary>有多个在线账号：收藏、文件窗口中显示账号切换。</summary>
    public bool HasMultipleOnline => OnlineEntries.Count > 1;

    // 账号增减、登录状态变化后重新生成在线列表（尽量不改动没有变化的项，避免下拉框选中项跳动）
    private void RefreshOnline()
    {
        var online = Entries.Where(e => e.IsOnline).ToList();
        for (var i = OnlineEntries.Count - 1; i >= 0; i--)
        {
            if (!online.Contains(OnlineEntries[i]))
                OnlineEntries.RemoveAt(i);
        }
        for (var i = 0; i < online.Count; i++)
        {
            if (i >= OnlineEntries.Count || OnlineEntries[i] != online[i])
            {
                OnlineEntries.Remove(online[i]);
                OnlineEntries.Insert(i, online[i]);
            }
        }
        OnPropertyChanged(nameof(HasMultipleOnline));
    }

    public void SetActive(AccountEntryViewModel? entry)
    {
        foreach (var e in Entries)
            e.IsActive = e == entry;
        Active = entry;
    }

    /// <summary>加到末尾（新登录的账号在数据库列表中也排在最后）。</summary>
    public void Add(AccountEntryViewModel entry)
    {
        entry.PropertyChanged += OnEntryPropertyChanged;
        Entries.Add(entry);
    }

    public void Remove(AccountEntryViewModel entry)
    {
        entry.PropertyChanged -= OnEntryPropertyChanged;
        entry.Detach();
        Entries.Remove(entry);
        if (Active == entry)
            Active = null;
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(ShowsAccountButtons));
        RefreshOnline();
    }

    // 启用 / 停用账号改变了主窗口中显示的账号数；登录、退出改变了在线账号
    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(AccountEntryViewModel.State) or ""))
            return;
        OnPropertyChanged(nameof(IsMulti));
        OnPropertyChanged(nameof(ShowsAccountButtons));
        RefreshOnline();
    }
}
