using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.ViewModels;

/// <summary>“Zulip 账号”页账号列表中的分组标题（不能选中）。</summary>
public sealed class AccountGroupHeader(string title)
{
    public string Title { get; } = title;
}

/// <summary>
/// 设置中“Zulip 账号”页的内容：左侧所有账号，中间选中账号的设置导航，右侧当前页。
/// 在线账号的设置由该账号的 <see cref="SettingsViewModel"/>（账号设置）承载（复用设置页，以及它的提示和确认框）；
/// 正在登录、登录失败、未启动的账号只显示基本信息、“启动”和“重新登录”或“登录”。
/// </summary>
public sealed partial class AccountsPanelViewModel : ViewModelBase
{
    private readonly Dictionary<AccountEntryViewModel, SettingsViewModel> _hosts = [];
    private readonly AccountGroupHeader _disabledHeader = new("未启用");
    private readonly HashSet<AccountEntryViewModel> _watched = [];

    // 设置窗口：选中的账号不在线时（没有页面宿主）在它上面显示确认框
    private readonly SettingsViewModel _owner;

    // 由本类切换选中的账号（已确认离开当前页），不再询问
    private bool _switching;

    public AccountsPanelViewModel(SettingsViewModel owner)
    {
        _owner = owner;
        Entries.CollectionChanged += OnEntriesChanged;
        RebuildItems();
    }

    public ObservableCollection<AccountEntryViewModel> Entries => AccountHubViewModel.Instance.Entries;

    /// <summary>左侧列表：启用的账号，然后是“未启用”标题和未启用的账号。</summary>
    public ObservableCollection<object> Items { get; } = [];

    /// <summary>左侧选中的账号（列表绑定）。</summary>
    [ObservableProperty]
    public partial AccountEntryViewModel? Selected { get; set; }

    /// <summary>选中的在线账号的页面宿主；不在线时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHost))]
    public partial SettingsViewModel? Host { get; private set; }

    public bool HasHost => Host is not null;

    /// <summary>当前页；切换账号时保持在同一页。</summary>
    [ObservableProperty]
    public partial SettingsPage Page { get; private set; } = SettingsPage.Server;

    /// <summary>选中某个账号并打开它的“服务器”（当前页有未保存的修改时先确认）。</summary>
    public void Show(AccountEntryViewModel entry) => _ = SwitchAsync(entry, SettingsPage.Server);

    public bool HasUnsavedChanges => Host?.CurrentPage?.HasUnsavedChanges == true;

    // 在左侧列表中点击了其他账号：当前页有未保存的修改时先选回原账号，确认放弃后再切换
    partial void OnSelectedChanged(AccountEntryViewModel? oldValue, AccountEntryViewModel? newValue)
    {
        if (oldValue is not null)
            oldValue.PropertyChanged -= OnSelectedPropertyChanged;
        if (newValue is not null)
            newValue.PropertyChanged += OnSelectedPropertyChanged;
        if (_switching)
            return;
        if (HasUnsavedChanges && oldValue is not null && Entries.Contains(oldValue))
        {
            // 列表正在更新选中项，稍后再选回
            Dispatcher.UIThread.Post(() =>
            {
                SetSelected(oldValue);
                _ = SwitchAsync(newValue, Page);
            });
            return;
        }
        Apply();
    }

    /// <summary>中间导航选择一页。</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private Task SelectPageAsync(SettingsNavItem item) => SwitchAsync(Selected, item.Page);

    // 切换账号或页：当前页有未保存的修改时先确认
    private async Task SwitchAsync(AccountEntryViewModel? entry, SettingsPage page)
    {
        if ((entry != Selected || page != Page) && Host is { } host && !await host.ConfirmLeaveAsync())
            return;
        Page = page;
        SetSelected(entry);
        Apply();
    }

    private void SetSelected(AccountEntryViewModel? entry)
    {
        _switching = true;
        try
        {
            Selected = entry;
        }
        finally
        {
            _switching = false;
        }
    }

    [RelayCommand]
    private void AddAccount() => App.Current.ShowAddAccount();

    /// <summary>登录失败的账号：打开“添加账号”登录窗重新登录。</summary>
    [RelayCommand]
    private void Relogin()
    {
        if (Selected is { IsFailed: true } entry)
            App.Current.SelectAccount(entry);
    }

    /// <summary>未启动的账号：在后台登录。</summary>
    [RelayCommand]
    private void Login()
    {
        if (Selected is { IsDisabled: true } entry)
            App.Current.LoginAccount(entry);
    }

    /// <summary>
    /// 删除账号（右侧的“删除账号”，或列表中右键“删除账号”）：先选中该账号（当前页有未保存的修改时先确认），再确认删除。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task DeleteAsync(AccountEntryViewModel? entry)
    {
        entry ??= Selected;
        if (entry is null)
            return;
        await SwitchAsync(entry, Page);
        if (Selected == entry)
            await DeleteAccountAsync(Host ?? _owner, entry);
    }

    /// <summary>
    /// 确认后删除账号：删除登录状态（清除保存的 key）并退出、移出列表；勾选“同时删除所有本地数据”时还删除该账号的所有本地记录。
    /// 确认框显示在 <paramref name="dialogs"/> 上。
    /// </summary>
    public static async Task DeleteAccountAsync(SettingsViewModel dialogs, AccountEntryViewModel entry)
    {
        var deleteData = await dialogs.ConfirmWithCheckAsync(
            $"确定要删除账号“{entry.DisplayName}”吗？\n将删除该账号的登录状态，再次登录需要重新输入密码。",
            "同时删除此账号的所有本地数据（聊天记录、设置、草稿等）", "删除", isDanger: true);
        if (deleteData is null || !AccountHubViewModel.Instance.Entries.Contains(entry))
            return;
        await App.Current.DeleteAccountAsync(entry, deleteData.Value);
    }

    /// <summary>
    /// 列表中右键“退出账号”：先选中该账号（当前页有未保存的修改时先确认），确认后退出。
    /// </summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task LogoutAsync(AccountEntryViewModel entry)
    {
        await SwitchAsync(entry, Page);
        if (Selected != entry)
            return;
        if (Host is { } host && !await host.ConfirmAsync($"确定要退出账号“{entry.DisplayName}”吗？", "退出", isDanger: true))
            return;
        if (Entries.Contains(entry))
            await App.Current.RemoveAccountAsync(entry);
    }

    public void Close()
    {
        Entries.CollectionChanged -= OnEntriesChanged;
        foreach (var entry in _watched)
            entry.PropertyChanged -= OnEntryPropertyChanged;
        _watched.Clear();
        if (Selected is not null)
            Selected.PropertyChanged -= OnSelectedPropertyChanged;
        foreach (var host in _hosts.Values)
            host.Close();
        _hosts.Clear();
        Host = null;
    }

    // 选中账号在线时显示它的页面；会话换过（重新登录）后旧的宿主作废
    private void Apply()
    {
        if (Selected is not { IsOnline: true, Session: { } session, Main: { } main } entry)
        {
            Host = null;
            return;
        }
        if (_hosts.TryGetValue(entry, out var host) && host.Session != session)
        {
            host.Close();
            _hosts.Remove(entry);
            host = null;
        }
        if (host is null)
        {
            host = new SettingsViewModel(session, main);
            _hosts[entry] = host;
        }
        host.Navigate(Page);
        Host = host;
    }

    private void OnSelectedPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountEntryViewModel.State) or "")
            Apply();
    }

    // 按“启动”分组；选中的账号保持选中（重建列表时列表会清除选中项）
    private void RebuildItems()
    {
        foreach (var entry in Entries.Where(e => _watched.Add(e)))
            entry.PropertyChanged += OnEntryPropertyChanged;
        foreach (var removed in _watched.Where(e => !Entries.Contains(e)).ToList())
        {
            removed.PropertyChanged -= OnEntryPropertyChanged;
            _watched.Remove(removed);
        }

        var enabled = Entries.Where(e => e.LoginOnStartup).ToList();
        var disabled = Entries.Where(e => !e.LoginOnStartup).ToList();
        List<object> items = [.. enabled];
        if (disabled.Count > 0)
            items.AddRange([_disabledHeader, .. disabled]);
        if (Items.SequenceEqual(items))
            return;
        var selected = Selected;
        _switching = true;
        try
        {
            Items.Clear();
            foreach (var item in items)
                Items.Add(item);
            Selected = selected;
        }
        finally
        {
            _switching = false;
        }
    }

    private void OnEntryPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(AccountEntryViewModel.LoginOnStartup) or "")
            RebuildItems();
    }

    // 删除的账号：释放它的页面；选中的账号被删除时改选第一个
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildItems();
        foreach (var removed in _hosts.Keys.Where(k => !Entries.Contains(k)).ToList())
        {
            _hosts[removed].Close();
            _hosts.Remove(removed);
        }
        if (Selected is not null && Entries.Contains(Selected))
            return;
        SetSelected(Items.OfType<AccountEntryViewModel>().FirstOrDefault());
        Apply();
    }
}
