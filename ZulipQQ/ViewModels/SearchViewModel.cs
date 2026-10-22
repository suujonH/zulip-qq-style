using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

/// <summary>全局搜索的一条结果。标题中与搜索词相同的部分高亮。</summary>
public sealed partial class SearchResultItem(string title, string? highlight, string subtitle, string? avatarUrl, string avatarText,
    IBrush? avatarFill, Action open) : ViewModelBase
{
    public string Title { get; } = title;

    public string? Highlight { get; } = highlight;

    public string Subtitle { get; } = subtitle;

    public string? AvatarUrl { get; } = avatarUrl;

    public string AvatarText { get; } = avatarText;

    public IBrush? AvatarFill { get; } = avatarFill;

    [RelayCommand]
    private void Open() => open();
}

/// <summary>全局搜索的一段（联系人 / 群聊 / 聊天记录）：默认显示前 3 条，“更多”就地展开全部。</summary>
public sealed partial class SearchSection(string title) : ViewModelBase
{
    private const int CollapsedCount = 3;

    private IReadOnlyList<SearchResultItem> _all = [];

    public string Title { get; } = title;

    public ObservableCollection<SearchResultItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMore))]
    public partial bool IsExpanded { get; private set; }

    /// <summary>聊天记录正在向服务器搜索。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsVisible))]
    public partial bool IsLoading { get; set; }

    public bool HasItems => _all.Count > 0;

    public bool HasMore => !IsExpanded && _all.Count > CollapsedCount;

    public bool IsVisible => HasItems || IsLoading;

    /// <summary>替换结果；<paramref name="keepExpanded"/> 为 false 时收起（搜索词变化时）。</summary>
    public void Set(IReadOnlyList<SearchResultItem> items, bool keepExpanded = false)
    {
        _all = items;
        if (!keepExpanded)
            IsExpanded = false;
        Show();
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasMore));
        OnPropertyChanged(nameof(IsVisible));
    }

    [RelayCommand]
    private void Expand()
    {
        IsExpanded = true;
        Show();
    }

    private void Show()
    {
        Items.Clear();
        foreach (var item in IsExpanded ? _all : _all.Take(CollapsedCount))
            Items.Add(item);
    }
}

/// <summary>
/// 主窗口搜索框的全局搜索。搜索词不为空时在会话列表的位置显示结果浮层：
/// <list type="bullet">
/// <item>联系人：本地按显示名、昵称、备注、邮箱匹配。</item>
/// <item>群聊：本地匹配话题名或备注、频道名；第一次搜索时依次加载所有已订阅频道的话题，加载一个更新一次。</item>
/// <item>聊天记录：服务器全文搜索（400ms 防抖），最近 200 条命中按会话分组，点击打开该会话的聊天记录窗口。</item>
/// </list>
/// </summary>
public partial class SearchViewModel : ViewModelBase
{
    private const int MaxLocalResults = 50;
    private const int MessageSearchSize = 200;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchTimer = new() { Interval = SearchDelay };
    private int _version;
    private bool _groupsRefreshPending;

    public SearchViewModel(ZulipSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _ = SearchMessagesAsync();
        };
        session.Channels.TopicsChanged += (_, _) => RequestGroupsRefresh();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsOpen))]
    public partial string Query { get; set; } = "";

    public bool IsOpen => Query.Trim().Length > 0;

    public SearchSection Contacts { get; } = new("联系人");

    public SearchSection Groups { get; } = new("群聊");

    public SearchSection Messages { get; } = new("聊天记录");

    public bool IsEmpty => IsOpen && !Contacts.HasItems && !Groups.HasItems && !Messages.IsVisible;

    /// <summary>需要打开聊天记录窗口（由主窗口执行）：会话与搜索词。</summary>
    public event EventHandler<(ConversationKey Key, string Query)>? HistoryRequested;

    /// <summary>关闭浮层：清空搜索框。</summary>
    public void Close() => Query = "";

    partial void OnQueryChanged(string value)
    {
        _version++;
        _searchTimer.Stop();
        var q = value.Trim();
        if (q.Length == 0)
        {
            Contacts.Set([]);
            Groups.Set([]);
            Messages.Set([]);
            Messages.IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }

        Contacts.Set(SearchContacts(q));
        Groups.Set(SearchGroups(q));
        Messages.Set([]);
        Messages.IsLoading = true;
        _searchTimer.Start();
        OnPropertyChanged(nameof(IsEmpty));
        _session.Forget(_session.Channels.EnsureAllTopicsLoadedAsync(), "加载全部话题");
    }

    // 话题列表陆续加载完成：合并为一次刷新，保留已展开的状态
    private void RequestGroupsRefresh()
    {
        if (!IsOpen || _groupsRefreshPending)
            return;
        _groupsRefreshPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _groupsRefreshPending = false;
            if (Query.Trim() is not { Length: > 0 } q)
                return;
            Groups.Set(SearchGroups(q), keepExpanded: true);
            OnPropertyChanged(nameof(IsEmpty));
        }, DispatcherPriority.Background);
    }

    private static bool Matches(string? text, string q) => text?.Contains(q, StringComparison.OrdinalIgnoreCase) == true;

    private List<SearchResultItem> SearchContacts(string q)
    {
        var users = _session.Users;
        var contacts = _session.Contacts;
        return users.ActiveHumans(excludeSelf: false).Concat(users.ActiveBots())
            .Where(u => Matches(contacts.DisplayName(u.Id), q) || Matches(u.FullName, q) || Matches(u.Email, q))
            .OrderByDescending(u => contacts.DisplayName(u.Id).StartsWith(q, StringComparison.OrdinalIgnoreCase))
            .ThenBy(u => contacts.DisplayName(u.Id), StringComparer.CurrentCulture)
            .Take(MaxLocalResults)
            .Select(u =>
            {
                var name = contacts.DisplayName(u.Id);
                var key = DmKey.Create([u.Id], _session.SelfId);
                return new SearchResultItem(name, q, u.Email, u.AvatarUrl, name, null, () => OpenChat(key));
            })
            .ToList();
    }

    private List<SearchResultItem> SearchGroups(string q)
    {
        var result = new List<SearchResultItem>();
        var conversations = _session.Conversations;
        foreach (var channel in _session.Channels.Subscribed().Where(c => Matches(c.Name, q)))
        {
            var topics = _session.Channels.TryGetTopics(channel.StreamId);
            var streamId = channel.StreamId;
            result.Add(new SearchResultItem("#" + channel.Name, q, topics is null ? "频道" : $"{topics.Count}个话题",
                null, channel.Name, ConversationItemViewModel.BrushOf(channel.Color), () => ShowChannel(streamId)));
        }

        var matches = new List<(TopicKey Key, string Name, string ChannelName, long MaxId)>();
        foreach (var channel in _session.Channels.Subscribed())
        {
            foreach (var topic in _session.Channels.TryGetTopics(channel.StreamId) ?? [])
            {
                var key = new TopicKey(channel.StreamId, topic.Name);
                if (Matches(topic.Name, q) || Matches(conversations.Get(key)?.Remark, q))
                    matches.Add((key, topic.Name, channel.Name, topic.MaxId));
            }
        }
        foreach (var (key, name, channelName, _) in matches.OrderByDescending(m => m.MaxId).Take(MaxLocalResults))
        {
            var title = conversations.Get(key)?.Remark is { Length: > 0 } remark ? remark
                : name.Length == 0 ? _session.Realm.EmptyTopicDisplayName : name;
            var (url, text, fill) = ConversationAvatars.Of(_session, key, title);
            result.Add(new SearchResultItem(title, q, $"频道：#{channelName}", url, text, fill, () => OpenTopic(key, name)));
        }
        return result;
    }

    private async Task SearchMessagesAsync()
    {
        var version = _version;
        var q = Query.Trim();
        if (q.Length == 0)
            return;

        GetMessagesResult result;
        try
        {
            result = await _session.Client.GetMessagesAsync(
                new MessageQuery([Narrow.Search(q)], "newest", MessageSearchSize, 0), _session.Token);
        }
        catch (Exception ex)
        {
            if (version != _version || _session.IsDisposed)
                return;
            Log.Warn("搜索聊天记录失败", ex);
            Messages.IsLoading = false;
            OnPropertyChanged(nameof(IsEmpty));
            return;
        }
        if (version != _version || _session.IsDisposed)
            return;

        // 结果被截断（还有更早的命中）时，各会话的条数都可能更多
        var more = result.Messages.Count >= MessageSearchSize && !result.FoundOldest ? "+" : "";
        var items = result.Messages
            .GroupBy(m => ConversationKey.FromMessage(m, _session.SelfId))
            .OrderByDescending(g => g.Max(m => m.Id))
            .Select(g =>
            {
                var key = g.Key;
                var title = _session.Conversations.GetTitle(key);
                var (url, text, fill) = ConversationAvatars.Of(_session, key, title);
                return new SearchResultItem(title, null, $"{g.Count()}{more}条相关聊天记录", url, text, fill,
                    () => OpenHistory(key, q));
            })
            .ToList();
        Messages.Set(items);
        Messages.IsLoading = false;
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OpenChat(ConversationKey key)
    {
        Close();
        _ = _main.OpenChatAsync(key);
    }

    private void OpenTopic(TopicKey key, string name)
    {
        // 从未打开过的话题也要有会话记录，标题、改名通知才有依据
        _session.Conversations.EnsureEntry(key, name);
        OpenChat(key);
    }

    private void ShowChannel(long streamId)
    {
        Close();
        _main.ShowContactsChannel(streamId);
    }

    private void OpenHistory(ConversationKey key, string q) => HistoryRequested?.Invoke(this, (key, q));
}
