using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>“联系人”页：好友 / 群聊两个标签，各自是一组可折叠的分组。</summary>
public partial class ContactsPageViewModel : ViewModelBase
{
    /// <summary>“我的好友”分组的 Id（显示全部用户），与自定义分组 Id（从 1 开始）不冲突。</summary>
    private const long AllFriendsSectionId = 0;

    private const long BotSectionId = -2;

    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private bool _refreshPending;

    public ContactsPageViewModel(ZulipSession session)
    {
        Session = session;
        FriendSections = BuildFriendSections();
        GroupSections = BuildGroupSections();

        // 分组、备注、用户资料、在线状态、频道变化后重建，并保留原来的展开状态
        session.Contacts.Changed += (_, _) => RequestRefresh();
        session.Users.UserChanged += (_, _) => RequestRefresh();
        session.Users.PresenceChanged += (_, _) => RequestRefresh();
        session.Users.Reset += (_, _) => RequestRefresh();
        session.Channels.ChannelChanged += (_, _) => RequestRefresh();
        session.Channels.Reset += (_, _) => RequestRefresh();
    }

    public ZulipSession Session { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial IReadOnlyList<ContactSectionViewModel> FriendSections { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial IReadOnlyList<ContactSectionViewModel> GroupSections { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSections))]
    public partial bool IsGroupTab { get; set; }

    public IReadOnlyList<ContactSectionViewModel> CurrentSections => IsGroupTab ? GroupSections : FriendSections;

    [RelayCommand]
    private void ShowFriends() => IsGroupTab = false;

    [RelayCommand]
    private void ShowGroups() => IsGroupTab = true;

    // 连续的多个变化合并为一次重建
    private void RequestRefresh()
    {
        if (_refreshPending)
            return;
        _refreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPending = false;
            FriendSections = Rebuild(FriendSections, BuildFriendSections());
            GroupSections = Rebuild(GroupSections, BuildGroupSections());
        }, DispatcherPriority.Background);
    }

    private static List<ContactSectionViewModel> Rebuild(IReadOnlyList<ContactSectionViewModel> old, List<ContactSectionViewModel> sections)
    {
        var expanded = old.Where(s => s.IsExpanded).Select(s => s.Id).ToHashSet();
        foreach (var section in sections)
            section.IsExpanded = expanded.Contains(section.Id);
        return sections;
    }

    // “我的好友”显示全部用户；自定义分组显示成员；有机器人时追加“机器人”分组（只显示总数）
    private List<ContactSectionViewModel> BuildFriendSections()
    {
        var now = DateTimeOffset.UtcNow;
        var contacts = Session.Contacts;
        var humans = Session.Users.ActiveHumans(excludeSelf: true).ToList();
        List<ContactSectionViewModel> sections =
        [
            FriendSection(AllFriendsSectionId, "我的好友", humans, now),
            .. contacts.Groups.Select(g =>
                FriendSection(g.Id, g.Name, humans.Where(u => contacts.GetGroupId(u.Id) == g.Id).ToList(), now)),
        ];

        var bots = Session.Users.ActiveBots().ToList();
        if (bots.Count > 0)
        {
            var items = Items(bots, now).Select(i => i.Item).ToList();
            sections.Add(new ContactSectionViewModel(BotSectionId, "机器人", items.Count.ToString(CultureInfo.InvariantCulture), items));
        }
        return sections;
    }

    // 人数显示“在线数/总数”，在线数包括离开
    private ContactSectionViewModel FriendSection(long id, string name, IReadOnlyList<ZulipUser> users, DateTimeOffset now)
    {
        var items = Items(users, now);
        var online = items.Count(i => i.Presence != PresenceState.Offline);
        return new ContactSectionViewModel(id, name, $"{online}/{items.Count}", items.Select(i => i.Item).ToList());
    }

    // 在线、离开、离线依次排列，同一状态内按显示名排序
    private List<(FriendItemViewModel Item, PresenceState Presence)> Items(IEnumerable<ZulipUser> users, DateTimeOffset now)
    {
        var contacts = Session.Contacts;
        return users
            .Select(u => (Friend: new Friend(u, contacts.GetGroupId(u.Id), contacts.GetRemark(u.Id)),
                Presence: Session.Users.GetPresence(u.Id, now)))
            .OrderByDescending(x => x.Presence)
            .ThenBy(x => x.Friend.DisplayName, NameComparer)
            .Select(x => (new FriendItemViewModel(x.Friend, x.Presence, Session.Users.GetStatusText(x.Friend.User.Id)), x.Presence))
            .ToList();
    }

    // 话题列表在联系人阶段按需加载，这里先列出已订阅的频道
    private List<ContactSectionViewModel> BuildGroupSections() =>
        Session.Channels.Subscribed()
            .Select(c => new ContactSectionViewModel(c.StreamId, c.Name,
                c.SubscriberCount.ToString(CultureInfo.InvariantCulture), []))
            .ToList();
}
