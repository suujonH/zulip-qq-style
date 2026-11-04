using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

public enum ReminderTab
{
    /// <summary>还没到时间。</summary>
    Pending,

    /// <summary>已经到时间（Notification Bot 发来的提醒私信）。</summary>
    Delivered,
}

/// <summary>一条消息提醒：待提醒的可以删除，已提醒的可以清除；都可以查看被提醒的消息。</summary>
public sealed partial class ReminderItemViewModel(RemindersViewModel owner, long? reminderId, long? targetMessageId,
    string timeText, string html, bool isFailed, long? noticeId = null) : ViewModelBase
{
    public string TimeText { get; } = timeText;

    public string Html { get; } = html;

    /// <summary>到时间后没有发送成功（例如消息已被删除）。</summary>
    public bool IsFailed { get; } = isFailed;

    public bool CanView => targetMessageId is not null;

    public bool CanDelete => reminderId is not null;

    /// <summary>已提醒的（Notification Bot 的提醒私信）可以清除。</summary>
    public bool CanClear => noticeId is not null;

    [RelayCommand]
    private Task ViewAsync() => targetMessageId is { } id ? owner.ViewMessageAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private Task DeleteAsync() => reminderId is { } id ? owner.DeleteAsync(id) : Task.CompletedTask;

    [RelayCommand]
    private Task ClearAsync() => noticeId is { } id ? owner.ClearAsync(id) : Task.CompletedTask;
}

/// <summary>
/// 消息提醒窗口：待提醒（服务器的 reminders）与已提醒（Notification Bot 的提醒私信）。
/// 查看“已提醒”时把提醒私信标为已读。
/// </summary>
public sealed partial class RemindersViewModel : ViewModelBase, IDialogHost
{
    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private bool _closed;
    private int _toastVersion;

    public RemindersViewModel(ZulipSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        session.Reminders.Changed += OnChanged;
        session.SystemNotices.Changed += OnChanged;
    }

    public ZulipSession Session => _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPending), nameof(IsDelivered), nameof(TabTitle))]
    public partial ReminderTab Tab { get; private set; }

    public bool IsPending => Tab == ReminderTab.Pending;

    public bool IsDelivered => Tab == ReminderTab.Delivered;

    public string TabTitle => IsPending ? "待提醒" : "已提醒";

    /// <summary>“已提醒”导航上的红点。</summary>
    public bool HasUnreadDelivered => _session.SystemNotices.UnreadReminderCount > 0;

    public ObservableCollection<ReminderItemViewModel> Items { get; } = [];

    /// <summary>列表中间的状态文字；有内容时为 null。</summary>
    public string? StatusText =>
        Items.Count > 0 ? null
        : IsPending ? "没有待提醒的消息"
        : _session.SystemNotices.IsLoading ? "加载中…"
        : "还没有到时间的提醒";

    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    /// <summary>需要把主窗口带到前台（查看消息之后）。</summary>
    public event EventHandler? MainWindowRequested;

    public void Start(ReminderTab tab)
    {
        Tab = tab;
        Rebuild();
        MarkReadIfDelivered();
    }

    public void Close()
    {
        _closed = true;
        _session.Reminders.Changed -= OnChanged;
        _session.SystemNotices.Changed -= OnChanged;
    }

    [RelayCommand]
    private void SelectTab(ReminderTab tab)
    {
        if (Tab == tab)
            return;
        Tab = tab;
        Rebuild();
        MarkReadIfDelivered();
    }

    /// <summary>在主窗口中打开被提醒的消息。</summary>
    public async Task ViewMessageAsync(long messageId)
    {
        if (await MessageLinks.CurrentKeyOfAsync(_session, messageId) is not { } key)
        {
            if (!_closed)
                ShowToast("消息不存在或无权查看");
            return;
        }
        await _main.OpenChatAsync(key, messageId);
        // 会话在独立窗口中打开时由该窗口显示在前台，不再激活主面板
        if (!_closed && _main.ShowsInRightPane(key))
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task DeleteAsync(long reminderId)
    {
        if (!await ConfirmAsync("删除这个提醒？", "删除", isDanger: true))
            return;
        try
        {
            await _session.Reminders.DeleteAsync(reminderId);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"删除提醒 {reminderId} 失败", ex);
            if (!_closed)
                ShowToast("删除失败");
        }
    }

    /// <summary>清除一条已到时间的提醒（只在本地不再显示）。</summary>
    public Task ClearAsync(long noticeId) => _session.SystemNotices.ClearReminderAsync(noticeId);

    /// <summary>“已提醒”标题右侧的“清除全部”。</summary>
    public bool CanClearAll => IsDelivered && Items.Count > 0;

    [RelayCommand]
    private async Task ClearAllAsync()
    {
        if (await ConfirmAsync("清除全部已提醒的消息？", "清除", isDanger: true))
            await _session.SystemNotices.ClearAllRemindersAsync();
    }

    /// <summary>提醒内容中的点击：组织内的链接在客户端中打开，其它链接用浏览器打开。</summary>
    public async Task HandleLinkAsync(MessageLinkTarget target)
    {
        if (target is UrlTarget url)
        {
            await MessageLinks.OpenUrlAsync(_session, _main.OpenChatAsync, url.Href, _main.OpenChannel, _main.ShowMissingTopic);
            if (!_closed)
                MainWindowRequested?.Invoke(this, EventArgs.Empty);
        }
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
        if (_closed)
            return;
        Rebuild();
        MarkReadIfDelivered();
    }

    private void MarkReadIfDelivered()
    {
        if (IsDelivered && _session.SystemNotices.UnreadReminderCount > 0)
            _ = _session.SystemNotices.MarkRemindersReadAsync();
    }

    private void Rebuild()
    {
        var now = DateTime.Now;
        var use24h = _session.Settings.Zulip.TwentyFourHourTime;
        Items.Clear();
        if (IsPending)
        {
            foreach (var r in _session.Reminders.Pending)
                Items.Add(new ReminderItemViewModel(this, r.ReminderId, r.ReminderTargetMessageId,
                    Core.TimeText.PreciseTime(Core.TimeText.FromUnix(r.ScheduledDeliveryTimestamp), now, use24h),
                    SystemNoticeText.LocalizeReminder(r.RenderedContent.Length > 0 ? r.RenderedContent : r.Content), r.Failed));
        }
        else
        {
            foreach (var e in _session.SystemNotices.DeliveredReminders)
                Items.Add(new ReminderItemViewModel(this, null, TargetOf(e.Html),
                    Core.TimeText.PreciseTime(Core.TimeText.FromUnix(e.Timestamp), now, use24h),
                    SystemNoticeText.LocalizeReminder(e.Html), isFailed: false, noticeId: e.Id));
        }
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasUnreadDelivered));
        OnPropertyChanged(nameof(CanClearAll));
    }

    // 提醒私信中“sent”链接指向被提醒的消息：…/near/{id}
    private static long? TargetOf(string html) =>
        NearPattern().Match(html) is { Success: true } m
            ? long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    [GeneratedRegex(@"/near/(\d+)")]
    private static partial Regex NearPattern();
}
