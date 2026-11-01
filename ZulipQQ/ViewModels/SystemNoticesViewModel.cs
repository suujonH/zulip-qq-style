using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>系统通知的筛选项；<see cref="Value"/> 为 null 表示全部。</summary>
public sealed record NoticeFilterOption(SystemNoticeCategory? Value, string Text);

/// <summary>系统通知中的一张卡片。头像为通知中提到的第一个用户（新成员、创建频道的人等），没有时为 Notification Bot。</summary>
public sealed partial class SystemNoticeItemViewModel : ViewModelBase
{
    private const int LongTextLength = 160;
    private const int LongLineCount = 4;

    public SystemNoticeItemViewModel(ZulipSession session, SystemNoticeEntry entry, DateTime now, bool isUnread)
    {
        Id = entry.Id;
        Html = entry.Html;
        var text = ZulipHtml.ToPlainText(entry.Html);
        IsLong = text.Length > LongTextLength || text.Count(ch => ch == '\n') >= LongLineCount;
        TimeText = Core.TimeText.ListTime(Core.TimeText.FromUnix(entry.Timestamp), now, session.Settings.Zulip.TwentyFourHourTime);
        CategoryText = SystemNoticeRules.CategoryText(entry.Category);
        IsUnread = isUnread;
        var user = entry.MentionedUserIds.Select(session.Users.Get).FirstOrDefault(u => u is not null);
        if (user is not null)
        {
            Title = session.Contacts.DisplayName(user.Id);
            AvatarUrl = user.AvatarUrl;
        }
        else
        {
            Title = "系统通知";
            AvatarUrl = session.Users.NotificationBotId is { } bot ? session.Users.Get(bot)?.AvatarUrl : null;
        }
    }

    public long Id { get; }

    public string Html { get; }

    /// <summary>内容超过约 4 行：默认收起，显示“展开”。</summary>
    public bool IsLong { get; }

    public string Title { get; }

    public string? AvatarUrl { get; }

    public string TimeText { get; }

    public string CategoryText { get; }

    /// <summary>打开系统通知时（或显示期间收到时）未读：显示红点，直到关闭。</summary>
    public bool IsUnread { get; }

    /// <summary>内容较长时默认收起。</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [RelayCommand]
    private void ToggleExpand() => IsExpanded = !IsExpanded;
}

/// <summary>
/// 系统通知：Notification Bot 的公告（新频道、新成员、Zulip 更新、举报）和发给自己的私信。
/// 默认模式显示在主窗口右侧，经典模式显示在独立窗口中。显示期间收到的通知立即标为已读。
/// </summary>
public sealed partial class SystemNoticesViewModel : ViewModelBase, IDialogHost
{
    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    // 打开以来未读过的通知，关闭前一直显示红点
    private readonly HashSet<long> _seenUnread = [];
    private bool _started;
    private bool _closed;
    private int _toastVersion;

    public SystemNoticesViewModel(ZulipSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        session.SystemNotices.Changed += OnChanged;
    }

    public ZulipSession Session => _session;

    public ObservableCollection<SystemNoticeItemViewModel> Items { get; } = [];

    /// <summary>当前的筛选分类；null 表示全部。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    public partial SystemNoticeCategory? Filter { get; private set; }

    public string FilterText => Filter is { } c ? SystemNoticeRules.CategoryText(c) : "全部";

    /// <summary>筛选菜单：全部，以及现有通知中出现过的分类。</summary>
    public IReadOnlyList<NoticeFilterOption> FilterOptions =>
    [
        new(null, "全部"),
        .. _session.SystemNotices.Notices.Select(e => e.Category).Distinct().Order()
            .Select(c => new NoticeFilterOption(c, SystemNoticeRules.CategoryText(c))),
    ];

    /// <summary>列表中间的状态文字；有内容时为 null。</summary>
    public string? StatusText =>
        Items.Count > 0 ? null
        : _session.SystemNotices.IsLoading ? "加载中…"
        : "暂无系统通知";

    public bool HasItems => _session.SystemNotices.Notices.Count > 0;

    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    /// <summary>开始显示：列出通知并全部标为已读。</summary>
    public void Start()
    {
        _started = true;
        Rebuild();
        _ = _session.SystemNotices.MarkNoticesReadAsync();
    }

    public void Close()
    {
        _closed = true;
        _session.SystemNotices.Changed -= OnChanged;
    }

    [RelayCommand]
    private void SelectFilter(SystemNoticeCategory? category)
    {
        Filter = category;
        Rebuild();
    }

    [RelayCommand]
    private async Task ClearAsync()
    {
        if (!HasItems || !await ConfirmAsync("清空后系统通知中的现有内容将不再显示（不会删除服务器上的消息）", "清空", isDanger: true))
            return;
        await _session.SystemNotices.ClearNoticesAsync();
    }

    /// <summary>通知内容中的点击：组织内的链接在客户端中打开，其它链接用浏览器打开。</summary>
    public async Task HandleLinkAsync(MessageLinkTarget target)
    {
        if (target is UrlTarget url)
            await MessageLinks.OpenUrlAsync(_session, _main.OpenChatAsync, url.Href, _main.OpenChannel, _main.ShowMissingTopic);
    }

    public void ShowToast(string text)
    {
        var version = ++_toastVersion;
        ToastText = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion)
                ToastText = null;
        }, TimeSpan.FromSeconds(2));
    }

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

    private void OnChanged(object? sender, EventArgs e)
    {
        if (_closed || !_started)
            return;
        Rebuild();
        // 显示期间收到的通知直接标为已读
        if (_session.SystemNotices.UnreadNoticeCount > 0)
            _ = _session.SystemNotices.MarkNoticesReadAsync();
    }

    private void Rebuild()
    {
        var now = DateTime.Now;
        var notices = _session.SystemNotices;
        _seenUnread.UnionWith(notices.Notices.Where(e => notices.IsUnread(e.Id)).Select(e => e.Id));
        var expanded = Items.Where(i => i.IsExpanded).Select(i => i.Id).ToHashSet();
        Items.Clear();
        foreach (var entry in _session.SystemNotices.Notices.Where(e => Filter is null || e.Category == Filter))
            Items.Add(new SystemNoticeItemViewModel(_session, entry, now, _seenUnread.Contains(entry.Id))
            {
                IsExpanded = expanded.Contains(entry.Id),
            });
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(FilterOptions));
    }
}
