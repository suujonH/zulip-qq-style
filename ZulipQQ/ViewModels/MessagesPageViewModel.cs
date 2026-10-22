using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
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
/// </summary>
public partial class MessagesPageViewModel : ViewModelBase
{
    private const string GroupDmAvatarColor = "#0052D9";

    private readonly ZulipSession _session;
    private readonly Action<ConversationKey> _openChat;
    private readonly Dictionary<ConversationKey, ConversationItemViewModel> _items = [];
    private bool _refreshPending;
    private bool _selecting;
    // 当前打开的会话；列表刷新后按它恢复选中（条目可能稍后才出现在列表中）
    private ConversationKey? _selectedKey;

    public MessagesPageViewModel(ZulipSession session, Action<ConversationKey> openChat)
    {
        _session = session;
        _openChat = openChat;

        session.Conversations.EntryChanged += (_, _) => RequestRefresh();
        session.Conversations.ListChanged += (_, _) => RequestRefresh();
        session.Conversations.Reset += (_, _) => RequestRefresh();
        session.Unread.ConversationChanged += (_, _) => RequestRefresh();
        session.Unread.Reset += (_, _) => RequestRefresh();
        session.Drafts.DraftChanged += (_, _) => RequestRefresh();
        session.Users.UserChanged += (_, _) => RequestRefresh();
        session.Users.Reset += (_, _) => RequestRefresh();
        session.Contacts.Changed += (_, _) => RequestRefresh();
        session.Channels.ChannelChanged += (_, _) => RequestRefresh();
        session.Channels.Reset += (_, _) => RequestRefresh();
        session.ConnectionChanged += (_, _) => UpdateConnection();

        Refresh();
        UpdateConnection();
    }

    /// <summary>主列表。</summary>
    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    /// <summary>群助手子列表。</summary>
    public ObservableCollection<ConversationItemViewModel> HelperConversations { get; } = [];

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedHelperConversation { get; set; }

    /// <summary>群助手子列表是否滑入显示。</summary>
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

    partial void OnSelectedConversationChanged(ConversationItemViewModel? value)
    {
        if (_selecting || value is null)
            return;
        if (value.IsHelper)
        {
            IsHelperOpen = true;
            // 群助手条目只是入口：列表正在提交选择，延后恢复原来的选中项
            Dispatcher.UIThread.Post(() => Select(_selectedKey));
            return;
        }
        Select(value.Key);
        _openChat(value.Key);
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
        var rows = store.Entries
            .Where(e => e.InList && e.Key is not HelperKey)
            .Select(e => (Entry: e, Level: store.GetLevel(e.Key)))
            .ToList();
        var assistant = rows.Where(r => r.Level == DndLevel.Assistant).Select(r => r.Entry).ToList();

        var main = rows.Where(r => r.Level != DndLevel.Assistant)
            .Select(r => (Item: ItemFor(r.Entry.Key, Snapshot(r.Entry, r.Level), now), r.Entry.PinnedAt, r.Entry.LastMessageTime))
            .ToList();
        if (assistant.Count > 0)
        {
            var helperEntry = store.Entries.FirstOrDefault(e => e.Key is HelperKey);
            var helper = HelperSnapshot(assistant, helperEntry?.IsPinned ?? false);
            main.Add((ItemFor(ConversationKey.Helper, helper, now), helperEntry?.PinnedAt, helper.LastMessageTime));
        }

        var helperItems = assistant
            .Select(e => (Item: ItemFor(e.Key, Snapshot(e, DndLevel.Assistant), now), e.PinnedAt, e.LastMessageTime))
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
        if (assistant.Count == 0)
            IsHelperOpen = false;

        // 不再出现在任何列表中的条目不再缓存
        var visible = Conversations.Concat(HelperConversations).Select(i => i.Key).ToHashSet();
        foreach (var key in _items.Keys.Where(k => !visible.Contains(k)).ToList())
            _items.Remove(key);

        var total = _session.Unread.TotalForBadge(store.GetLevel);
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
        item.Update(snapshot, now);
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
            title,
            e.LastMessageTime,
            preview,
            _session.Drafts.Get(key),
            _session.Unread.Count(key),
            _session.Unread.GetNotice(key),
            level,
            e.IsPinned,
            avatarUrl,
            avatarText,
            avatarColor);
    }

    /// <summary>群助手条目：时间取各会话的最新时间，提示取最高优先级的 @，预览为“N个群聊有新消息”。</summary>
    private ConversationSnapshot HelperSnapshot(IReadOnlyList<ConversationEntry> assistant, bool isPinned)
    {
        var unreadCount = assistant.Count(e => _session.Unread.Count(e.Key) > 0);
        var notices = assistant.Select(e => _session.Unread.GetNotice(e.Key)).ToList();
        var notice = notices.Contains(ConversationNotice.AtMe) ? ConversationNotice.AtMe
            : notices.Contains(ConversationNotice.AtAll) ? ConversationNotice.AtAll
            : ConversationNotice.None;
        return new ConversationSnapshot(
            ConversationKey.Helper,
            "群助手",
            assistant.Max(e => e.LastMessageTime),
            unreadCount > 0 ? $"{unreadCount}个群聊有新消息" : "",
            null,
            0,
            notice,
            DndLevel.Notify,
            isPinned,
            null,
            "群助手",
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
