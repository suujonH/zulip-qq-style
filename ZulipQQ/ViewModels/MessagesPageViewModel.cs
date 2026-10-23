using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “消息”页：会话列表。置顶的在前（最后置顶的在最上面），其余按最后消息时间倒序。
/// 级别为“收进群助手且不提醒”的会话收进群助手子列表，主列表中用一个“群助手”条目代替。
/// 开启“收起已解决话题”时，已解决的话题（优先于群助手）同样收进“已解决话题”子列表。
/// </summary>
public partial class MessagesPageViewModel : ViewModelBase
{
    internal const string GroupDmAvatarColor = "#0052D9";

    private readonly ZulipSession _session;
    private readonly Action<ConversationKey> _openChat;
    private readonly Func<ConversationKey, Task> _closeChat;
    private readonly Action<string> _showToast;
    private readonly Dictionary<ConversationKey, ConversationItemViewModel> _items = [];
    private bool _refreshPending;
    private bool _selecting;
    // 当前打开的会话；列表刷新后按它恢复选中（条目可能稍后才出现在列表中）
    private ConversationKey? _selectedKey;

    public MessagesPageViewModel(ZulipSession session, Action<ConversationKey> openChat,
        Func<ConversationKey, Task> closeChat, Action<string> showToast)
    {
        _session = session;
        _openChat = openChat;
        _closeChat = closeChat;
        _showToast = showToast;

        session.Conversations.EntryChanged += (_, _) => RequestRefresh();
        session.Conversations.ListChanged += (_, _) => RequestRefresh();
        session.Conversations.Reset += (_, _) => RequestRefresh();
        session.Unread.ConversationChanged += (_, _) => RequestRefresh();
        session.Unread.Reset += (_, _) => RequestRefresh();
        session.Drafts.DraftChanged += (_, _) => RequestRefresh();
        session.Registry.Changed += (_, _) => RequestRefresh();
        session.Users.UserChanged += (_, _) => RequestRefresh();
        session.Users.Reset += (_, _) => RequestRefresh();
        session.Contacts.Changed += (_, _) => RequestRefresh();
        session.Channels.ChannelChanged += (_, _) => RequestRefresh();
        session.Channels.Reset += (_, _) => RequestRefresh();
        session.Settings.Changed += (_, name) =>
        {
            if (name is "" or "twenty_four_hour_time")
                RequestRefresh();
        };
        session.Settings.Local.Changed += (_, key) =>
        {
            if (key is LocalSettings.TopicTitleFormatKey or LocalSettings.CollapseResolvedTopicsKey)
                RequestRefresh();
        };
        session.ConnectionChanged += (_, _) => UpdateConnection();

        Refresh();
        UpdateConnection();
    }

    /// <summary>主列表。</summary>
    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    /// <summary>会话夹子列表：当前打开的会话夹（<see cref="OpenFolder"/>）中的会话。</summary>
    public ObservableCollection<ConversationItemViewModel> HelperConversations { get; } = [];

    /// <summary>子列表显示的会话夹：群助手或已解决话题。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FolderTitle))]
    public partial ConversationKey OpenFolder { get; private set; } = ConversationKey.Helper;

    /// <summary>子列表标题行右侧的灰色文字。</summary>
    public string FolderTitle => _session.Conversations.GetTitle(OpenFolder);

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedHelperConversation { get; set; }

    /// <summary>会话夹子列表是否滑入显示。</summary>
    [ObservableProperty]
    public partial bool IsHelperOpen { get; set; }

    [ObservableProperty]
    public partial string? ConnectionText { get; private set; }

    [ObservableProperty]
    public partial bool IsConnectionBannerVisible { get; private set; }

    /// <summary>导航栏“消息”按钮上的未读总数（只统计允许提醒的会话），超过 99 显示 99+。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTotalUnread))]
    public partial string TotalUnreadText { get; private set; } = "";

    public bool HasTotalUnread => TotalUnreadText.Length > 0;

    /// <summary>选中某个会话（从其它入口打开聊天时调用），不再触发打开。传入 null 清除选择。</summary>
    /// <summary>当前选中的会话。</summary>
    public ConversationKey? SelectedKey => _selectedKey;

    public void Select(ConversationKey? key)
    {
        _selectedKey = key;
        _selecting = true;
        try
        {
            ApplySelection();
        }
        finally
        {
            _selecting = false;
        }
    }

    private void ApplySelection()
    {
        SelectedConversation = _selectedKey is null ? null : Conversations.FirstOrDefault(c => c.Key.Equals(_selectedKey));
        SelectedHelperConversation = _selectedKey is null ? null : HelperConversations.FirstOrDefault(c => c.Key.Equals(_selectedKey));
    }

    [RelayCommand]
    private void CloseHelper() => IsHelperOpen = false;

    // ---- 会话右键菜单 ----

    /// <summary>话题和多人私聊有四级“群消息设置”；一对一私聊只有“消息免打扰”开关。</summary>
    public static bool HasGroupLevels(ConversationItemViewModel item) => item.Key is TopicKey or DmKey { IsGroupDm: true };

    public bool CanMarkRead(ConversationItemViewModel item) => !item.IsFolder && _session.Unread.Count(item.Key) > 0;

    public bool CanMarkUnread(ConversationItemViewModel item) =>
        !item.IsFolder && !item.IsBlocked && _session.Unread.Count(item.Key) == 0
        && _session.Conversations.Get(item.Key) is { LastMessageId: > 0 };

    public void TogglePin(ConversationItemViewModel item) => _session.Conversations.SetPinned(item.Key, !item.IsPinned);

    public async Task MarkReadAsync(ConversationItemViewModel item)
    {
        try
        {
            await _session.Chat.MarkConversationReadAsync(item.Key);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"标为已读失败：{item.Key.ToStorageString()}", ex);
            _showToast("操作失败");
        }
    }

    /// <summary>把最后一条消息标为未读（未读数和提示由 update_message_flags 事件更新）。</summary>
    public async Task MarkUnreadAsync(ConversationItemViewModel item)
    {
        if (_session.Conversations.Get(item.Key) is not { LastMessageId: > 0 and var id })
            return;
        try
        {
            await _session.Client.UpdateFlagsAsync([id], "remove", "read", _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"标为未读失败：{item.Key.ToStorageString()}", ex);
            _showToast("操作失败");
        }
    }

    public async Task SetLevelAsync(ConversationItemViewModel item, DndLevel level)
    {
        if (item.Level == level)
            return;
        try
        {
            await _session.Conversations.SetLevelAsync(item.Key, level);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"设置免打扰失败：{item.Key.ToStorageString()}", ex);
            _showToast("设置失败");
        }
    }

    /// <summary>从消息列表中移除；正在右侧打开时同时关闭聊天。</summary>
    public async Task RemoveAsync(ConversationItemViewModel item)
    {
        _session.Conversations.Remove(item.Key);
        await _closeChat(item.Key);
    }

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (_selecting || value is null)
            return;
        if (value.IsFolder)
        {
            // 会话夹条目只是入口：列表正在提交选择，延后填充子列表、滑入，并恢复原来的选中项
            OpenFolder = value.Key;
            Dispatcher.UIThread.Post(() =>
            {
                if (_session.IsDisposed)
                    return;
                Refresh();
                IsHelperOpen = HelperConversations.Count > 0;
                Select(_selectedKey);
            });
            return;
        }
        Select(value.Key);
        _openChat(value.Key);
    }

    /// <summary>再次点击已选中的会话：重新打开（已在独立窗口中时激活该窗口）。</summary>
    public void Reopen(ConversationItemViewModel item)
    {
        if (!item.IsFolder)
            _openChat(item.Key);
    }

    partial void OnSelectedHelperConversationChanged(ConversationItemViewModel? value)
    {
        if (_selecting || value is null)
            return;
        Select(value.Key);
        _openChat(value.Key);
    }

    // 连续的多个变化合并为一次刷新
    private void RequestRefresh()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (!_session.IsDisposed)
                Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        var now = DateTime.Now;
        var store = _session.Conversations;
        // 收起的已解决话题优先于群助手：用 GetEffectiveLevel（屏蔽的保持屏蔽，其余按群助手显示）
        var rows = store.Entries
            .Where(e => e.InList && e.Key is not FolderKey)
            .Select(e => (Entry: e, Level: store.GetEffectiveLevel(e.Key), Resolved: store.IsCollapsedResolved(e.Key)))
            .ToList();
        var resolved = rows.Where(r => r.Resolved).ToList();
        var assistant = rows.Where(r => !r.Resolved && r.Level == DndLevel.Assistant).ToList();

        var main = rows.Where(r => !r.Resolved && r.Level != DndLevel.Assistant)
            .Select(r => (Item: ItemFor(r.Entry.Key, Snapshot(r.Entry, r.Level), now), r.Entry.PinnedAt, r.Entry.LastMessageTime))
            .ToList();
        foreach (var (folder, members) in new[] { (ConversationKey.Helper, assistant), (ConversationKey.Resolved, resolved) })
        {
            if (members.Count == 0)
                continue;
            var folderEntry = store.Get(folder);
            var snapshot = FolderSnapshot(folder, members.Select(r => r.Entry).ToList(), folderEntry?.IsPinned ?? false);
            main.Add((ItemFor(folder, snapshot, now), folderEntry?.PinnedAt, snapshot.LastMessageTime));
        }

        var openMembers = OpenFolder is ResolvedKey ? resolved : assistant;
        var helperItems = openMembers
            .Select(r => (Item: ItemFor(r.Entry.Key, Snapshot(r.Entry, r.Level), now), r.Entry.PinnedAt, r.Entry.LastMessageTime))
            .ToList();

        _selecting = true;
        try
        {
            CollectionSync.SyncTo(Conversations, Sort(main));
            CollectionSync.SyncTo(HelperConversations, Sort(helperItems));
            ApplySelection();
        }
        finally
        {
            _selecting = false;
        }
        if (openMembers.Count == 0)
            IsHelperOpen = false;

        // 不再出现在任何列表中的条目不再缓存
        var visible = Conversations.Concat(HelperConversations).Select(i => i.Key).ToHashSet();
        foreach (var key in _items.Keys.Where(k => !visible.Contains(k)).ToList())
            _items.Remove(key);

        var total = _session.Unread.TotalForBadge(store.GetEffectiveLevel);
        TotalUnreadText = total == 0 ? "" : total > 99 ? "99+" : total.ToString(CultureInfo.InvariantCulture);
    }

    // 置顶的在前（最后置顶的在最上面），其余按最后消息时间倒序；时间相同按存储字符串
    private static List<ConversationItemViewModel> Sort(
        IEnumerable<(ConversationItemViewModel Item, long? PinnedAt, long Time)> rows) =>
        rows.OrderByDescending(r => r.PinnedAt is not null)
            .ThenByDescending(r => r.PinnedAt ?? 0)
            .ThenByDescending(r => r.Time)
            .ThenBy(r => r.Item.Key.ToStorageString(), StringComparer.Ordinal)
            .Select(r => r.Item)
            .ToList();

    private ConversationItemViewModel ItemFor(ConversationKey key, ConversationSnapshot snapshot, DateTime now)
    {
        if (!_items.TryGetValue(key, out var item))
            _items[key] = item = new ConversationItemViewModel(key);
        item.Update(snapshot, now, _session.Settings.Zulip.TwentyFourHourTime);
        return item;
    }

    private ConversationSnapshot Snapshot(ConversationEntry e, DndLevel level)
    {
        var key = e.Key;
        // 话题和多人私聊的预览带发送者（自己发的不带）
        var preview = e.LastPreview;
        var multiParty = key is TopicKey or DmKey { IsGroupDm: true };
        if (multiParty && e.LastMessageId > 0 && e.LastSenderId != _session.SelfId)
            preview = $"{_session.Contacts.DisplayName(e.LastSenderId)}：{preview}";

        var title = _session.Conversations.GetTitle(key);
        var (avatarUrl, avatarText, avatarColor) = key switch
        {
            TopicKey t => (null, title, _session.Channels.Get(t.StreamId)?.Color),
            DmKey { IsSelfDm: true } => (_session.Users.Get(_session.SelfId)?.AvatarUrl, title, null),
            DmKey { OtherUserIds.Length: 1 } d => (_session.Users.Get(d.OtherUserIds[0])?.AvatarUrl, title, null),
            _ => ((string?)null, "群", GroupDmAvatarColor),
        };

        return new ConversationSnapshot(
            key,
            _session.Conversations.GetDisplayTitle(key),
            e.LastMessageTime,
            preview,
            // 正在聊天视图中打开的会话不显示草稿（草稿就在输入框里）
            _session.Registry.IsOpen(key) ? null : _session.Drafts.Get(key),
            _session.Unread.Count(key),
            _session.Unread.GetNotice(key),
            level,
            e.IsPinned,
            avatarUrl,
            avatarText,
            avatarColor);
    }

    /// <summary>会话夹条目：时间取各会话的最新时间，提示取最高优先级的 @，预览为“N个群聊有新消息”。</summary>
    private ConversationSnapshot FolderSnapshot(ConversationKey folder, IReadOnlyList<ConversationEntry> members, bool isPinned)
    {
        var unreadCount = members.Count(e => _session.Unread.Count(e.Key) > 0);
        var notices = members.Select(e => _session.Unread.GetNotice(e.Key)).ToList();
        var notice = notices.Contains(ConversationNotice.AtMe) ? ConversationNotice.AtMe
            : notices.Contains(ConversationNotice.AtAll) ? ConversationNotice.AtAll
            : ConversationNotice.None;
        var title = _session.Conversations.GetTitle(folder);
        return new ConversationSnapshot(
            folder,
            title,
            members.Max(e => e.LastMessageTime),
            unreadCount > 0 ? $"{unreadCount}个群聊有新消息" : "",
            null,
            0,
            notice,
            DndLevel.Notify,
            isPinned,
            null,
            title,
            null);
    }

    private void UpdateConnection()
    {
        ConnectionText = _session.Connection switch
        {
            ConnectionState.Reconnecting => "连接已断开，正在重连…",
            ConnectionState.AuthFailed => "登录已失效",
            _ => null,
        };
        IsConnectionBannerVisible = ConnectionText is not null;
    }
}
