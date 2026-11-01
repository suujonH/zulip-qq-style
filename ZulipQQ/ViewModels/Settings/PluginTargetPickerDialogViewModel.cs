using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Plugins;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>
/// 为用户 / 话题设置项选择对象：列出所有已登录账号的用户（不含自己和机器人），或已订阅频道的话题（没有缓存的频道在打开后加载）。
/// 选择后以选中的对象调用 onClosed，关闭时传入 null。
/// </summary>
public sealed partial class PluginTargetPickerDialogViewModel : ViewModelBase, IDisposable
{
    private readonly Action<PluginTarget?> _onClosed;
    private readonly bool _showAccount;
    private bool _closed;

    public PluginTargetPickerDialogViewModel(PluginTargetKind kind, string title, Action<PluginTarget?> onClosed)
    {
        _onClosed = onClosed;
        Title = title;
        var sessions = PluginHost.Instance.Sessions;
        _showAccount = sessions.Count > 1;
        foreach (var session in sessions)
        {
            if (kind == PluginTargetKind.User)
                AddUsers(session);
            else
                AddTopics(session);
        }
    }

    public string Title { get; }

    private ObservableCollection<PluginTargetPickerItem> Items { get; } = [];

    /// <summary>筛选后的项（名称或说明包含 <see cref="Filter"/>，不区分大小写）。</summary>
    public IReadOnlyList<PluginTargetPickerItem> VisibleItems => string.IsNullOrWhiteSpace(Filter)
        ? Items
        : Items.Where(i => i.Title.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)
                           || i.Subtitle.Contains(Filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    public bool HasNoVisibleItems => VisibleItems.Count == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VisibleItems), nameof(HasNoVisibleItems))]
    public partial string? Filter { get; set; }

    [RelayCommand]
    private void Pick(PluginTargetPickerItem item) => Close(item.Target);

    [RelayCommand]
    private void Cancel() => Close(null);

    public void Dispose() => Close(null);

    private void Close(PluginTarget? target)
    {
        if (_closed)
            return;
        _closed = true;
        _onClosed(target);
    }

    private string AccountSuffix(ZulipSession session) => _showAccount ? $" · {session.Account.Email}" : "";

    private void AddUsers(ZulipSession session)
    {
        foreach (var user in session.Users.ActiveHumans(excludeSelf: true).OrderBy(u => session.Contacts.DisplayName(u.Id)))
        {
            var name = session.Contacts.DisplayName(user.Id);
            Items.Add(new PluginTargetPickerItem(PluginTarget.ForUser(session.Account.Id, user.Id, name), name,
                user.Email + AccountSuffix(session)));
        }
    }

    // 有缓存的频道直接列出，其余在后台加载后追加
    private void AddTopics(ZulipSession session)
    {
        foreach (var channel in session.Channels.Subscribed().OrderBy(c => c.Name))
        {
            if (session.Channels.TryGetTopics(channel.StreamId) is { } topics)
                AddTopics(session, channel.StreamId, channel.Name, topics);
            else
                LoadTopics(session, channel.StreamId, channel.Name);
        }
    }

    private async void LoadTopics(ZulipSession session, long streamId, string channelName)
    {
        try
        {
            var topics = await session.Channels.GetTopicsAsync(streamId);
            if (!_closed)
                AddTopics(session, streamId, channelName, topics);
        }
        catch (Exception ex)
        {
            Log.Warn($"加载话题失败：{channelName}", ex);
        }
    }

    private void AddTopics(ZulipSession session, long streamId, string channelName, IReadOnlyList<Models.TopicInfo> topics)
    {
        foreach (var topic in topics)
        {
            Items.Add(new PluginTargetPickerItem(PluginTarget.ForTopic(session.Account.Id, streamId, topic.Name, $"{channelName} / {topic.Name}"),
                topic.Name, channelName + AccountSuffix(session)));
        }
        OnPropertyChanged(nameof(VisibleItems));
        OnPropertyChanged(nameof(HasNoVisibleItems));
    }
}

/// <summary>选择列表的一项。</summary>
public sealed record PluginTargetPickerItem(PluginTarget Target, string Title, string Subtitle);
