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
using ZulipQQ.ViewModels.Settings;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “联系人”页：好友 / 群聊两个标签，各自是一组可折叠的分组。
/// 单击条目在右侧显示资料，双击打开聊天。分组和条目在数据刷新时按标识复用、就地更新。
/// </summary>
public partial class ContactsPageViewModel : ViewModelBase
{
    /// <summary>“我的好友”分组的 Id（显示全部用户），与自定义分组 Id（从 1 开始）不冲突。</summary>
    private const long AllFriendsSectionId = 0;

    private const long PinnedTopicsSectionId = -1;

    private const long BotSectionId = -2;

    private const long PinnedFriendsSectionId = -3;

    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private readonly MainViewModel _main;
    private readonly Dictionary<long, FriendItemViewModel> _friendItems = [];
    private readonly Dictionary<TopicKey, TopicItemViewModel> _topicItems = [];
    private bool _refreshPending;

    // 选中的条目：用户 id（long）或 TopicKey；刷新后按它恢复选中
    private object? _selected;

    public ContactsPageViewModel(ZulipSession session, MainViewModel main)
    {
        Session = session;
        _main = main;
        Refresh();

        // 分组、备注、置顶、用户资料、在线状态、频道、话题变化后刷新
        session.Contacts.Changed += (_, _) => RequestRefresh();
        session.Users.UserChanged += (_, _) => RequestRefresh();
        session.Users.PresenceChanged += (_, _) => RequestRefresh();
        session.Users.Reset += (_, _) => RequestRefresh();
        session.Channels.ChannelChanged += (_, _) => RequestRefresh();
        session.Channels.TopicsChanged += (_, _) => RequestRefresh();
        session.Channels.Reset += (_, _) => RequestRefresh();
        // 全局的推送设置与服务器是否开启手机推送决定群聊列表中的推送效果
        session.Settings.Changed += (_, _) => RequestRefresh();
        session.PermissionsChanged += (_, _) => RequestRefresh();
        session.Conversations.ListChanged += (_, _) => RequestRefresh();
        session.Conversations.EntryChanged += (_, _) => RequestRefresh();
        session.Conversations.Reset += (_, _) => RequestRefresh();
        session.SystemNotices.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(HasUnreadNotices));
            OnPropertyChanged(nameof(HasUnreadReminders));
        };
        session.Reminders.Changed += (_, _) => OnPropertyChanged(nameof(PendingReminderText));
    }

    public ZulipSession Session { get; }

    public ObservableCollection<ContactSectionViewModel> FriendSections { get; } = [];

    public ObservableCollection<ContactSectionViewModel> GroupSections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial bool IsGroupTab { get; set; }

    public ObservableCollection<ContactSectionViewModel> CurrentSections => IsGroupTab ? GroupSections : FriendSections;

    /// <summary>“系统通知”条目的红点。</summary>
    public bool HasUnreadNotices => Session.SystemNotices.UnreadNoticeCount > 0;

    /// <summary>“消息提醒”条目的红点：有到时间、尚未查看的提醒。</summary>
    public bool HasUnreadReminders => Session.SystemNotices.UnreadReminderCount > 0;

    /// <summary>待提醒的数量；没有时为 null。</summary>
    public string? PendingReminderText => Session.Reminders.Pending.Count is > 0 and var n ? n.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>右侧正在显示系统通知（“系统通知”条目显示为选中）。</summary>
    [ObservableProperty]
    public partial bool IsNoticesSelected { get; set; }

    [RelayCommand]
    private void ShowSystemNotices() => _main.ShowSystemNotices();

    /// <summary>右侧显示了系统通知：取消其它条目的选中。</summary>
    public void MarkSystemNoticesSelected()
    {
        ClearSelection();
        IsNoticesSelected = true;
    }

    [RelayCommand]
    private void ShowFriends() => IsGroupTab = false;

    [RelayCommand]
    private void ShowGroups() => IsGroupTab = true;

    /// <summary>切换到群聊标签并展开频道分组（全局搜索中点击频道）。返回该分组；频道未订阅时为 null。</summary>
    public ContactSectionViewModel? ShowChannel(long streamId)
    {
        IsGroupTab = true;
        var section = GroupSections.FirstOrDefault(s => s.Kind == ContactSectionKind.Channel && s.Id == streamId);
        if (section is not null)
            section.IsExpanded = true;
        return section;
    }

    /// <summary>单击：选中条目并在右侧显示资料。</summary>
    public void Select(ViewModelBase item)
    {
        if (CreateCard(item) is not { } card)
            return;
        _selected = item switch
        {
            FriendItemViewModel f => f.UserId,
            TopicItemViewModel t => t.Key,
            _ => null,
        };
        ApplySelection();
        _ = _main.ShowPaneAsync(new ProfilePaneViewModel(card));
    }

    /// <summary>条目的名片（资料面板，或经典模式下单击弹出的名片）；<paramref name="onOpened"/> 在点击“发消息”后调用。</summary>
    public ViewModelBase? CreateCard(ViewModelBase item, Action? onOpened = null)
    {
        switch (item)
        {
            case FriendItemViewModel f:
                return new UserCardViewModel(Session, f.UserId, OpenChat, onOpened);
            case TopicItemViewModel t:
                // 确保有会话记录，话题被改名或移动时名片才能收到通知
                Session.Conversations.EnsureEntry(t.Key, Session.Conversations.GetTopicName(t.Key));
                return new TopicCardViewModel(Session, t.Key, OpenChat, onOpened);
            default:
                return null;
        }
    }

    /// <summary>双击：打开聊天。</summary>
    public void Open(ViewModelBase item)
    {
        switch (item)
        {
            case FriendItemViewModel f:
                OpenChat(DmKey.Create([f.UserId], Session.SelfId));
                break;
            case TopicItemViewModel t:
                OpenChat(t.Key);
                break;
        }
    }

    /// <summary>右侧不再显示所选条目的资料时（例如打开了聊天）清除选中。</summary>
    public void ClearSelection()
    {
        _selected = null;
        ApplySelection();
    }

    private void OpenChat(ConversationKey key) => _ = _main.OpenChatAsync(key);

    // ---- 分组标题的操作 ----

    public void ShowAddGroupDialog()
    {
        AddGroupDialogViewModel dialog = null!;
        dialog = new AddGroupDialogViewModel(Session.Contacts, _ => _main.Dialogs.Remove(dialog));
        _main.Dialogs.Add(dialog);
    }

    // ---- 备注（只保存在本地）----

    public void ShowUserRemarkDialog(long userId)
    {
        var name = Session.Users.Get(userId)?.FullName ?? "未知用户";
        var contacts = Session.Contacts;
        ShowRemarkDialog(contacts.GetRemark(userId), name, remark => contacts.SetRemark(userId, remark));
    }

    public void ShowChannelRemarkDialog(long streamId)
    {
        var name = Session.Channels.Get(streamId)?.Name ?? "";
        ShowRemarkDialog(Session.Contacts.GetChannelRemark(streamId), $"#{name}",
            remark => Session.Channels.SetRemark(streamId, remark));
    }

    public void ShowTopicRemarkDialog(TopicKey key)
    {
        var conversations = Session.Conversations;
        ShowRemarkDialog(conversations.Get(key)?.Remark, conversations.GetOriginalTopicTitle(key),
            remark => conversations.SetRemark(key, remark));
    }

    private void ShowRemarkDialog(string? remark, string originalName, Action<string?> save)
    {
        RemarkDialogViewModel dialog = null!;
        dialog = new RemarkDialogViewModel(RemarkMenuText(remark).TrimEnd('…'), originalName, remark, save,
            () => _main.Dialogs.Remove(dialog));
        _main.Dialogs.Add(dialog);
    }

    /// <summary>右键菜单中的文字：没有备注时“设置备注…”，已有备注时“修改备注…”。</summary>
    public static string RemarkMenuText(string? remark) => string.IsNullOrEmpty(remark) ? "设置备注…" : "修改备注…";

    public async Task DeleteGroupAsync(ContactSectionViewModel section)
    {
        if (await _main.ConfirmAsync("删除后，该分组中的好友将移至我的好友", "删除"))
            Session.Contacts.DeleteGroup(section.Id);
    }

    /// <summary>修改频道的个人订阅设置（颜色）。结果由 subscription 事件更新。</summary>
    public async Task SetChannelPropertyAsync(long streamId, string property, object value)
    {
        try
        {
            await Session.Client.UpdateSubscriptionPropertiesAsync([new SubscriptionPropertyChange(streamId, property, value)], Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"修改频道设置失败：{streamId} {property}", ex);
            _main.ShowToast("设置失败");
        }
    }

    public void SetLevel(TopicKey key, DndLevel level)
    {
        if (Session.Conversations.GetLevel(key) != level)
            Session.Conversations.SetLevel(key, level);
    }

    public async Task UnsubscribeAsync(Channel channel)
    {
        if (!await _main.ConfirmAsync($"确定要退订频道 #{channel.Name} 吗？", "退订"))
            return;
        try
        {
            await Session.Client.UnsubscribeAsync([channel.Name], null, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"退订频道失败：{channel.StreamId}", ex);
            _main.ShowToast("退订失败");
        }
    }

    // ---- 刷新 ----

    // 连续的多个变化合并为一次刷新
    private void RequestRefresh()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            if (!Session.IsDisposed)
                Refresh();
        }, DispatcherPriority.Background);
    }

    private void Refresh()
    {
        RefreshFriends();
        RefreshGroups();
        ApplySelection();
    }

    // “置顶好友”（有置顶的好友时显示）；“我的好友”显示全部用户；自定义分组显示成员；有机器人时追加“机器人”分组（只显示总数）
    private void RefreshFriends()
    {
        var now = DateTimeOffset.UtcNow;
        var contacts = Session.Contacts;
        var humans = Session.Users.ActiveHumans(excludeSelf: true).ToList();
        var used = new HashSet<long>();

        List<ContactSectionViewModel> desired = [];
        // 联系人页自己的置顶（与消息列表的置顶无关）；已停用的用户不显示
        var shown = humans.Concat(Session.Users.ActiveBots()).ToDictionary(u => u.Id);
        var pinned = contacts.PinnedFriends().Where(shown.ContainsKey).Select(id => shown[id]).ToList();
        if (pinned.Count > 0)
            desired.Add(FriendSection(ContactSectionKind.PinnedFriends, PinnedFriendsSectionId, "置顶好友", pinned, now, used));
        desired.Add(FriendSection(ContactSectionKind.AllFriends, AllFriendsSectionId, "我的好友", humans, now, used));
        foreach (var g in contacts.Groups)
        {
            var section = FriendSection(ContactSectionKind.FriendGroup, g.Id, g.Name,
                humans.Where(u => contacts.GetGroupId(u.Id) == g.Id).ToList(), now, used);
            section.Renamed ??= name => Session.Contacts.RenameGroup(section.Id, name);
            desired.Add(section);
        }

        // Notification Bot 的消息在系统通知中查看
        var bots = Session.Users.ActiveBots().Where(u => u.Id != Session.Users.NotificationBotId).ToList();
        if (bots.Count > 0)
        {
            var items = FriendItems(bots, now, used).Select(i => i.Item).ToList<ViewModelBase>();
            var section = SectionFor(FriendSections, ContactSectionKind.Bots, BotSectionId);
            section.Update("机器人", items.Count.ToString(CultureInfo.InvariantCulture), items);
            desired.Add(section);
        }

        CollectionSync.SyncTo(FriendSections, desired);
        foreach (var id in _friendItems.Keys.Where(id => !used.Contains(id)).ToList())
            _friendItems.Remove(id);
    }

    // 人数显示“在线数/总数”，在线数包括离开
    private ContactSectionViewModel FriendSection(ContactSectionKind kind, long id, string name, IReadOnlyList<ZulipUser> users,
        DateTimeOffset now, HashSet<long> used)
    {
        var items = FriendItems(users, now, used);
        var online = items.Count(i => i.Presence != PresenceState.Offline);
        var section = SectionFor(FriendSections, kind, id);
        section.Update(name, $"{online}/{items.Count}", items.Select(i => i.Item).ToList<ViewModelBase>());
        return section;
    }

    // 在线、离开、离线依次排列，同一状态内按显示名排序
    private List<(FriendItemViewModel Item, PresenceState Presence)> FriendItems(IEnumerable<ZulipUser> users, DateTimeOffset now,
        HashSet<long> used)
    {
        var contacts = Session.Contacts;
        return users
            .Select(u => (Friend: new Friend(u, contacts.GetGroupId(u.Id), contacts.GetRemark(u.Id)),
                Presence: Session.Users.GetPresence(u.Id, now)))
            .OrderByDescending(x => x.Presence)
            .ThenBy(x => x.Friend.DisplayName, NameComparer)
            .Select(x =>
            {
                var id = x.Friend.User.Id;
                used.Add(id);
                if (!_friendItems.TryGetValue(id, out var item))
                    _friendItems[id] = item = new FriendItemViewModel(id);
                item.Update(x.Friend, x.Presence, Session.Users.GetStatusText(id));
                return (item, x.Presence);
            })
            .ToList();
    }

    // “置顶群聊”（有置顶的话题时显示），然后每个已订阅频道一组；频道的话题第一次展开时加载
    private void RefreshGroups()
    {
        var used = new HashSet<TopicKey>();
        List<ContactSectionViewModel> desired = [];

        // 联系人页自己的置顶（与消息列表的置顶无关），最后置顶的在前。
        // 保存的键中话题名是小写的，换成实际的话题名，打开聊天、发消息时才是正确的话题
        var pinned = Session.Contacts.PinnedTopics()
            .Where(t => Session.Channels.Get(t.StreamId) is { IsSubscribed: true })
            .Select(t => TopicItem(new TopicKey(t.StreamId, Session.Conversations.GetTopicName(t)), used))
            .ToList<ViewModelBase>();
        if (pinned.Count > 0)
        {
            var section = SectionFor(GroupSections, ContactSectionKind.PinnedTopics, PinnedTopicsSectionId);
            section.Update("置顶话题", "", pinned);
            desired.Add(section);
        }

        // 已归档的频道不再显示（与网页端左侧栏一致）
        foreach (var channel in Session.Channels.Subscribed().Where(c => !c.IsArchived))
        {
            var section = SectionFor(GroupSections, ContactSectionKind.Channel, channel.StreamId);
            var streamId = channel.StreamId;
            if (section.Push is { } push)
                push.Refresh();
            else
                section.Push = new PushChannelGroupViewModel(Session, streamId, _main.ShowToast);
            if (Session.Channels.TryGetTopics(streamId) is { } topics)
            {
                var items = topics.Select(t => TopicItem(new TopicKey(streamId, t.Name), used)).ToList<ViewModelBase>();
                section.Update(Session.Channels.NameWithOriginal(streamId), "", items,
                    isLocked: channel.InviteOnly, isDimmed: channel.IsMuted);
            }
            else
            {
                section.Update(Session.Channels.NameWithOriginal(streamId), "", [], () => LoadTopicsAsync(streamId),
                    isLocked: channel.InviteOnly, isDimmed: channel.IsMuted);
            }
            desired.Add(section);
        }

        CollectionSync.SyncTo(GroupSections, desired);
        foreach (var key in _topicItems.Keys.Where(k => !used.Contains(k)).ToList())
            _topicItems.Remove(key);
    }

    private TopicItemViewModel TopicItem(TopicKey key, HashSet<TopicKey> used)
    {
        used.Add(key);
        if (!_topicItems.TryGetValue(key, out var item))
            _topicItems[key] = item = new TopicItemViewModel(key);
        item.Update(Session.Conversations.GetTopicTitleWithOriginal(key), Session.Channels.Get(key.StreamId)?.Color);
        if (item.Push is { } push)
            push.Refresh();
        else
            item.Push = new PushTopicRowViewModel(Session, key.StreamId, key.Topic, _main.ShowToast);
        return item;
    }

    // 加载结果由 TopicsChanged 触发的刷新填入分组
    private async Task<bool> LoadTopicsAsync(long streamId)
    {
        try
        {
            await Session.Channels.GetTopicsAsync(streamId);
            return true;
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"加载话题列表失败：{streamId}", ex);
            _main.ShowToast("加载话题失败");
            return false;
        }
    }

    private static ContactSectionViewModel SectionFor(IEnumerable<ContactSectionViewModel> existing, ContactSectionKind kind, long id) =>
        existing.FirstOrDefault(s => s.Kind == kind && s.Id == id) ?? new ContactSectionViewModel(kind, id);

    private void ApplySelection()
    {
        foreach (var item in _friendItems.Values)
            item.IsSelected = _selected is long id && id == item.UserId;
        foreach (var item in _topicItems.Values)
            item.IsSelected = _selected is TopicKey key && key.Equals(item.Key);
    }
}
