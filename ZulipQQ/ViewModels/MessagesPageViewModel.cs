using System;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>“消息”页：会话列表。置顶的在前（最后置顶的在最上面），其余按最后消息时间倒序。</summary>
public partial class MessagesPageViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private bool _refreshPending;

    public MessagesPageViewModel(ZulipSession session)
    {
        _session = session;

        session.Conversations.EntryChanged += (_, _) => RequestRefresh();
        session.Conversations.ListChanged += (_, _) => RequestRefresh();
        session.Conversations.Reset += (_, _) => RequestRefresh();
        session.Unread.ConversationChanged += (_, _) => RequestRefresh();
        session.Unread.Reset += (_, _) => RequestRefresh();
        session.Drafts.DraftChanged += (_, _) => RequestRefresh();
        session.Users.UserChanged += (_, _) => RequestRefresh();
        session.Contacts.Changed += (_, _) => RequestRefresh();
        session.Channels.ChannelChanged += (_, _) => RequestRefresh();
        session.ConnectionChanged += (_, _) => UpdateConnection();

        Refresh();
        UpdateConnection();
    }

    public ObservableCollection<ConversationItemViewModel> Conversations { get; } = [];

    [ObservableProperty]
    public partial ConversationItemViewModel? SelectedConversation { get; set; }

    [ObservableProperty]
    public partial string? ConnectionText { get; private set; }

    [ObservableProperty]
    public partial bool IsConnectionBannerVisible { get; private set; }

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
        var existing = Conversations.ToDictionary(c => c.Key);
        var desired = _session.Conversations.Entries
            .Where(e => e.InList && e.Key is not HelperKey)
            .OrderByDescending(e => e.PinnedAt is not null)
            .ThenByDescending(e => e.PinnedAt ?? 0)
            .ThenByDescending(e => e.LastMessageTime)
            .ThenBy(e => e.Key.ToStorageString(), StringComparer.Ordinal)
            .Select(e =>
            {
                if (!existing.TryGetValue(e.Key, out var item))
                    item = new ConversationItemViewModel(e.Key);
                item.Update(Snapshot(e), now);
                return item;
            })
            .ToList();

        CollectionSync.SyncTo(Conversations, desired);
        if (SelectedConversation is not null && !Conversations.Contains(SelectedConversation))
            SelectedConversation = null;
    }

    private ConversationSnapshot Snapshot(ConversationEntry e)
    {
        var key = e.Key;
        // 话题和多人私聊的预览带发送者（自己发的不带）
        var preview = e.LastPreview;
        var multiParty = key is TopicKey or DmKey { IsGroupDm: true };
        if (multiParty && e.LastMessageId > 0 && e.LastSenderId != _session.SelfId)
            preview = $"{_session.Contacts.DisplayName(e.LastSenderId)}：{preview}";

        return new ConversationSnapshot(
            key,
            _session.Conversations.GetTitle(key),
            e.LastMessageTime,
            preview,
            _session.Drafts.Get(key),
            _session.Unread.Count(key),
            _session.Unread.GetNotice(key),
            _session.Conversations.GetLevel(key),
            e.IsPinned);
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
