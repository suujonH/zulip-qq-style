using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 一个打开的会话（主窗口右侧或独立窗口）。负责消息列表、时间分隔条、“新消息”分隔线，
/// 以及右上角的“N条新消息 / @我”和右下角的“N条新消息”按钮。滚动由视图执行。
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(1.5);

    private readonly ZulipSession _session;
    private readonly Func<ConversationKey, long?, Task> _openChat;
    private readonly Action<string> _showToast;

    private readonly NewMessagesDividerItem _divider = new();
    private readonly LoadingItem _loadingItem = new();
    private Dictionary<long, MessageItemViewModel> _vms = [];
    private Dictionary<long, TimeSeparatorItem> _separators = [];

    // 打开时的未读快照：决定“新消息”分隔线和右上角按钮，之后不再随已读变化
    private SortedSet<long> _unreadAtOpen = [];
    private long? _firstUnreadId;
    private List<(long Id, MentionKind Kind)> _mentionQueue = [];

    // 曾经出现在视口顶部的最小消息 id；视图第一次报告前为 MaxValue
    private long _minTopSeen = long.MaxValue;
    private int _newWhileScrolledUp;
    private bool _isAtBottom = true;
    private long? _timeShownMessageId;

    private bool _loadingOlder;
    private bool _loadingNewer;
    private bool _snapshotTaken;
    private bool _opened;

    // 消息至少成功加载过一次；加载失败时什么都没显示，不能标记已读
    private bool _loaded;
    private bool _closed;

    public ChatViewModel(ZulipSession session, MainViewModel main, ConversationKey key)
    {
        _session = session;
        Main = main;
        _openChat = (k, f) => main.OpenChatAsync(k, f);
        _showToast = main.ShowToast;
        Key = key;
        Composer = new ComposerViewModel(this, session, _showToast);
        Sidebar = key is TopicKey ? new TopicSidebarViewModel(this) : null;
        RefreshTitle();
    }

    public MainViewModel Main { get; }

    /// <summary>话题聊天的右侧栏（群公告、群聊成员）；私聊为 null。</summary>
    public TopicSidebarViewModel? Sidebar { get; }

    public ComposerViewModel Composer { get; }

    /// <summary>
    /// 标题栏“…”打开的更多面板。每次打开新建（回到主页面）；关闭后保留到滑出动画结束，但不再随数据刷新。
    /// </summary>
    [ObservableProperty]
    public partial ChatMorePanelViewModel? MorePanel { get; private set; }

    [ObservableProperty]
    public partial bool IsMorePanelOpen { get; private set; }

    [RelayCommand]
    private void ToggleMorePanel()
    {
        if (IsMorePanelOpen)
        {
            CloseMorePanel();
            return;
        }
        // 先标记打开：视图换上新面板时据此开始刷新
        IsMorePanelOpen = true;
        MorePanel = new ChatMorePanelViewModel(this, Main);
    }

    [RelayCommand]
    private void CloseMorePanel()
    {
        IsMorePanelOpen = false;
        MorePanel?.Detach();
    }

    /// <summary>话题被整体移动时会被替换。</summary>
    public ConversationKey Key { get; private set; }

    public ZulipSession Session => _session;

    /// <summary>话题和多人私聊显示发送者昵称。</summary>
    public bool ShowsSenderNames => Key is TopicKey or DmKey { IsGroupDm: true };

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    public ObservableCollection<ViewModelBase> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsTopBannerVisible))]
    public partial string? TopBannerText { get; private set; }

    public bool IsTopBannerVisible => TopBannerText is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBottomBannerVisible))]
    public partial string? BottomBannerText { get; private set; }

    public bool IsBottomBannerVisible => BottomBannerText is not null;

    /// <summary>加载失败时显示在消息区中间的提示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorText { get; private set; }

    public bool HasError => ErrorText is not null;

    /// <summary>气泡最大宽度（消息区宽度的 65%），由视图在尺寸变化时设置。</summary>
    [ObservableProperty]
    public partial double BubbleMaxWidth { get; set; } = 480;

    /// <summary>所在窗口，由视图挂载时设置；用于判断是否正在查看。</summary>
    public Window? HostWindow { get; set; }

    public event EventHandler<ScrollRequest>? ScrollRequested;

    /// <summary>打开完成时的滚动位置；视图在会话打开之后才挂载（或换到另一个窗口）时用它定位。</summary>
    public ScrollRequest? InitialScroll { get; private set; }

    /// <summary>
    /// 打开会话：取未读快照、加载消息、滚动到底部（或指定消息），正在查看时标记已读。
    /// 每次 await 返回后检查是否已关闭。
    /// </summary>
    public async Task OpenAsync(long? focusMessageId)
    {
        _session.Registry.Register(this);
        Subscribe();

        var snapshot = _session.Unread.Snapshot(Key);
        _unreadAtOpen = [.. snapshot.Ids];
        _firstUnreadId = snapshot.Ids.Count > 0 ? snapshot.Ids.Min() : null;
        _mentionQueue = [.. snapshot.Mentions.OrderByDescending(m => m.Id)];
        _snapshotTaken = true;
        Composer.Open();
        _session.Forget(_session.Emoji.EnsureLoadedAsync(), "加载表情");

        try
        {
            var timeline = _session.Messages.TryGet(Key);
            if (focusMessageId is { } focus)
                await _session.Messages.LoadAroundAsync(Key, focus);
            else if (timeline is not { FoundNewest: true } || timeline.Messages.Count < 30)
                await _session.Messages.LoadNewestAsync(Key);
            _loaded = true;
        }
        catch (Exception ex)
        {
            if (!_session.IsDisposed)
            {
                Log.Warn($"加载消息失败：{Key}", ex);
                ErrorText = "消息加载失败";
            }
        }
        if (_closed || _session.IsDisposed)
            return;

        _opened = true;
        RebuildItems();
        InitialScroll = focusMessageId is { } id ? new ScrollRequest(id) : ScrollRequest.Bottom;
        ScrollRequested?.Invoke(this, InitialScroll);
        if (focusMessageId is { } flash)
            Flash(flash);
        UpdateTopBanner();
        MarkReadIfViewing();
    }

    /// <summary>关闭会话（幂等）：取消订阅、从打开列表中移除，并等待草稿保存完成。</summary>
    public Task CloseAsync()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        CloseMorePanel();
        ExitMultiSelect();
        Unsubscribe();
        _session.Registry.Unregister(this);
        return Composer.CloseAsync();
    }

    /// <summary>重新加载最新的消息并滚动到底部（事件队列重建、话题被移动后）。</summary>
    [RelayCommand]
    public async Task ReloadAsync()
    {
        if (_closed)
            return;
        try
        {
            await _session.Messages.LoadNewestAsync(Key);
            ErrorText = null;
            _loaded = true;
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"加载消息失败：{Key}", ex);
            ErrorText = "消息加载失败";
        }
        if (_closed || _session.IsDisposed)
            return;
        _opened = true;
        _newWhileScrolledUp = 0;
        RebuildItems();
        ScrollRequested?.Invoke(this, ScrollRequest.Bottom);
        UpdateBottomBanner();
        MarkReadIfViewing();
    }

    /// <summary>话题被整体移动或改名：换成新键并重新加载。</summary>
    public void Rekey(ConversationKey newKey)
    {
        var oldKey = Key;
        ExitMultiSelect();
        Key = newKey;
        RefreshTitle();
        Composer.OnKeyChanged(oldKey);
        _ = ReloadAsync();
    }

    /// <summary>新消息会出现在列表末尾：消息已加载，且当前窗口包含最新的消息。</summary>
    public bool ShowsLatest => _loaded && !_closed && _session.Messages.TryGet(Key) is { FoundNewest: true };

    /// <summary>
    /// 本会话收到新消息（在 MessageStore 处理之后调用）。正在查看较早的历史时消息不会追加到列表，
    /// 这里计入右下角的“N条新消息”。
    /// </summary>
    public void OnIncomingMessage(ChatMessage m)
    {
        if (!_opened || _closed || m.SenderId == _session.SelfId || ShowsLatest)
            return;
        _newWhileScrolledUp++;
        UpdateBottomBanner();
    }

    /// <summary>所在窗口被激活：补标已读（打开时窗口不在前台，或在后台收到了新消息）。</summary>
    public void OnHostActivated()
    {
        if (_snapshotTaken && !_closed)
            MarkReadIfViewing();
    }

    /// <summary>视图报告滚动后的可见范围。</summary>
    public void OnViewportChanged(ViewportInfo v)
    {
        if (!_opened || _closed)
            return;
        _isAtBottom = v.IsAtBottom;
        if (v.TopMessageId > 0)
        {
            _minTopSeen = Math.Min(_minTopSeen, v.TopMessageId);
            _mentionQueue.RemoveAll(m => m.Id >= v.TopMessageId && m.Id <= v.BottomMessageId);
        }

        var timeline = _session.Messages.TryGet(Key);
        if (v.IsNearTop && timeline is { FoundOldest: false } && !_loadingOlder)
            LoadOlder();
        if (v.IsNearBottom && timeline is { FoundNewest: false } && !_loadingNewer)
            LoadNewer();
        // 在较早历史的底部时新消息还没有加载，提示保留到加载到最新端为止
        if (v.IsAtBottom && timeline is { FoundNewest: true })
            _newWhileScrolledUp = 0;

        UpdateTopBanner();
        UpdateBottomBanner();
    }

    /// <summary>跳转到某条消息：不在当前窗口中时加载它附近的消息。</summary>
    public async Task JumpToMessageAsync(long id, bool highlight = true)
    {
        var timeline = _session.Messages.TryGet(Key);
        if (timeline is null || !timeline.Messages.Exists(m => m.Id == id))
        {
            try
            {
                await _session.Messages.LoadAroundAsync(Key, id);
            }
            catch (Exception ex)
            {
                if (!_session.IsDisposed)
                    Log.Warn($"加载消息失败：{id}", ex);
                return;
            }
            if (_closed)
                return;
            RebuildItems();
        }

        var align = !highlight && id == _firstUnreadId ? ScrollAlign.TopWithDivider : ScrollAlign.Center;
        ScrollRequested?.Invoke(this, new ScrollRequest(id, align));
        if (highlight)
            Flash(id);
    }

    /// <summary>点击气泡：显示 / 隐藏精确时间，同一时间只有一条显示。</summary>
    public void ShowTime(long id)
    {
        var previous = _timeShownMessageId;
        _timeShownMessageId = previous == id ? null : id;
        if (previous is { } p && _vms.TryGetValue(p, out var old))
            old.IsTimeShown = false;
        if (_timeShownMessageId is { } current && _vms.TryGetValue(current, out var vm))
            vm.IsTimeShown = true;
    }

    /// <summary>用户名片；用户不存在（例如已被删除）时返回 null。</summary>
    public UserCardViewModel? CreateUserCard(long userId, Action onOpened) =>
        _session.Users.Get(userId) is null ? null : new UserCardViewModel(_session, userId, k => _ = _openChat(k, null), onOpened);

    /// <summary>点击标题弹出的名片：话题为话题名片，一对一私聊为对方的名片，与自己的私聊为自己的名片，多人私聊没有名片。</summary>
    public ViewModelBase? CreateTitleCard(Action onOpened) => Key switch
    {
        TopicKey t => new TopicCardViewModel(_session, t, k => _ = _openChat(k, null), onOpened),
        DmKey { OtherUserIds.Length: 0 } => CreateUserCard(_session.SelfId, onOpened),
        DmKey { OtherUserIds.Length: 1 } d => CreateUserCard(d.OtherUserIds[0], onOpened),
        _ => null,
    };

    /// <summary>处理消息内容中的点击：链接、文件。图片查看器和提及的用户名片由视图打开。</summary>
    public async Task HandleLinkAsync(MessageItemViewModel? item, MessageLinkTarget target, TopLevel? top)
    {
        switch (target)
        {
            case UrlTarget url:
                await MessageLinks.OpenUrlAsync(_session, _openChat, url.Href);
                break;
            case FileTarget file:
                await MessageLinks.HandleFileAsync(_session, file, Key, item?.Id ?? 0, top, _showToast);
                break;
        }
    }

    /// <summary>
    /// 图片查看器的图片列表：当前已加载的消息中的全部图片（按时间先后），以及点击的那张的位置。
    /// 点击的图片不在消息中（例如群公告里的图片）时只有它一张。
    /// </summary>
    public (IReadOnlyList<InlineImage> Images, int Index) CollectImages(ImageTarget clicked)
    {
        var images = new List<InlineImage>();
        foreach (var m in _session.Messages.TryGet(Key)?.Messages ?? [])
        {
            if (!m.IsHiddenByPlugin && m.Id > 0 && !(m.SenderId != _session.SelfId && _session.Users.IsMuted(m.SenderId)))
                images.AddRange(ZulipHtml.ExtractInlineImages(m.ContentHtml));
        }
        var index = images.FindIndex(i => i.Src == clicked.Src && i.Href == clicked.Href);
        return index >= 0 ? (images, index) : ([new InlineImage(clicked.Src, clicked.Href)], 0);
    }

    /// <summary>消息链接（复制链接用）。</summary>
    public string MessageLink(long messageId)
    {
        var channel = Key is TopicKey t ? _session.Channels.Get(t.StreamId)?.Name ?? "" : "";
        var topic = Key is TopicKey tk ? _session.Conversations.GetTopicName(tk) : "";
        return ZulipLinks.MessageLink(_session.Client.RealmUrl, Key, _session.SelfId, messageId, channel, topic);
    }

    // ---- 多选 ----

    private readonly HashSet<long> _checked = [];

    /// <summary>多选模式：消息左侧显示复选框，点击消息切换勾选，输入区换成操作栏。</summary>
    [ObservableProperty]
    public partial bool IsMultiSelect { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasChecked))]
    [NotifyCanExecuteChangedFor(nameof(ForwardEachCommand), nameof(ForwardMergedCommand), nameof(StarCheckedCommand))]
    public partial int CheckedCount { get; private set; }

    public bool HasChecked => CheckedCount > 0;

    /// <summary>进入多选模式，并勾选右键的那条消息。</summary>
    public void BeginMultiSelect(MessageItemViewModel first)
    {
        IsMultiSelect = true;
        CloseMorePanel();
        if (first.Id > 0 && _checked.Add(first.Id))
            first.IsChecked = true;
        CheckedCount = _checked.Count;
    }

    [RelayCommand]
    public void ExitMultiSelect()
    {
        foreach (var id in _checked)
        {
            if (_vms.TryGetValue(id, out var vm))
                vm.IsChecked = false;
        }
        _checked.Clear();
        CheckedCount = 0;
        IsMultiSelect = false;
    }

    /// <summary>多选模式下点击消息：切换勾选（发送中的本地回显不能勾选）。</summary>
    public void ToggleChecked(MessageItemViewModel item)
    {
        if (!IsMultiSelect || item.Id <= 0)
            return;
        if (!_checked.Remove(item.Id))
            _checked.Add(item.Id);
        item.IsChecked = _checked.Contains(item.Id);
        CheckedCount = _checked.Count;
    }

    /// <summary>转发一条消息（右键菜单）。</summary>
    public void Forward(MessageItemViewModel item) => ShowForward([item.Message], merge: false);

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private void ForwardEach() => ForwardChecked(merge: false);

    [RelayCommand(CanExecute = nameof(HasChecked))]
    private void ForwardMerged() => ForwardChecked(merge: true);

    /// <summary>收藏勾选的消息，然后退出多选。</summary>
    [RelayCommand(CanExecute = nameof(HasChecked))]
    private async Task StarCheckedAsync()
    {
        var ids = CheckedMessages().Select(m => m.Id).ToList();
        ExitMultiSelect();
        try
        {
            await _session.Chat.SetStarredAsync(ids, true);
            _showToast("已收藏");
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("收藏失败", ex);
            _showToast("收藏失败");
        }
    }

    private void ForwardChecked(bool merge)
    {
        var messages = CheckedMessages();
        ExitMultiSelect();
        if (messages.Count > 0)
            ShowForward(messages, merge);
    }

    // 勾选的消息按 id 升序（即发送顺序）；已不在时间线中的（被删除、移走）跳过
    private List<ChatMessage> CheckedMessages() =>
        (_session.Messages.TryGet(Key)?.Messages ?? []).Where(m => _checked.Contains(m.Id)).OrderBy(m => m.Id).ToList();

    private void ShowForward(IReadOnlyList<ChatMessage> messages, bool merge)
    {
        var session = _session;
        var names = messages.Select(m => session.Users.Get(m.SenderId) is null ? m.SenderName : session.Contacts.DisplayName(m.SenderId)).ToList();
        string preview;
        if (merge)
            preview = $"[聊天记录] 共{messages.Count}条";
        else if (messages.Count == 1)
            preview = $"{names[0]}：{ZulipHtml.ToPreview(messages[0].ContentHtml)}";
        else
            preview = $"[逐条转发] 共{messages.Count}条消息";

        async Task<IReadOnlyList<string>> BuildAsync()
        {
            var raws = new List<string>();
            foreach (var m in messages)
                raws.Add(await session.Chat.GetRawContentAsync(m.Id));
            if (!merge)
                return raws;
            var forwarded = messages.Select((m, i) => new ForwardedMessage(names[i], TimeText.FromUnix(m.Timestamp), raws[i])).ToList();
            return ForwardFormatter.Merge(forwarded, session.Realm.MaxMessageLength);
        }

        ForwardDialogViewModel.Show(Main, preview, BuildAsync);
    }

    internal Task ToggleReactionAsync(MessageItemViewModel item, ReactionViewModel reaction) =>
        ToggleReactionAsync(item, reaction.Name, reaction.Code, reaction.Type);

    /// <summary>消息右键菜单中选择表情：切换回应，并计入常用表情。</summary>
    public Task ReactAsync(MessageItemViewModel item, EmojiEntry emoji)
    {
        _session.Emoji.AddRecent(emoji);
        return ToggleReactionAsync(item, emoji.Name, emoji.Code, emoji.Type);
    }

    /// <summary>收藏 / 取消收藏（星标）。</summary>
    public async Task ToggleStarAsync(MessageItemViewModel item)
    {
        if (item.Id <= 0)
            return;
        try
        {
            await _session.Chat.SetStarredAsync([item.Id], !item.Message.IsStarred);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("收藏失败", ex);
            _showToast("操作失败");
        }
    }

    /// <summary>重发发送失败的消息。</summary>
    internal void Retry(MessageItemViewModel item)
    {
        if (item.Message.LocalId is { } localId)
            _session.Forget(_session.Chat.RetryAsync(localId), "重发消息");
    }

    private async Task ToggleReactionAsync(MessageItemViewModel item, string name, string code, string type)
    {
        if (item.Id <= 0)
            return;
        try
        {
            await _session.Chat.ToggleReactionAsync(item.Message, name, code, type);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("表情回应失败", ex);
            _showToast("操作失败");
        }
    }

    /// <summary>
    /// 右上角按钮：有未看到的提及时依次跳转到最新的一条并高亮；否则跳到第一条未读，让“新消息”分隔线位于顶部。
    /// </summary>
    [RelayCommand]
    private async Task TopBannerClickAsync()
    {
        if (_mentionQueue.Count > 0)
        {
            var mention = _mentionQueue[0];
            _mentionQueue.RemoveAt(0);
            await JumpToMessageAsync(mention.Id, highlight: true);
        }
        else if (_firstUnreadId is { } first)
        {
            await JumpToMessageAsync(first, highlight: false);
            _minTopSeen = Math.Min(_minTopSeen, first);
        }
        UpdateTopBanner();
    }

    /// <summary>右下角按钮：回到最新的消息。</summary>
    [RelayCommand]
    private Task BottomBannerClickAsync() => ShowLatestAsync();

    /// <summary>回到最新的消息：当前窗口不包含最新的消息时重新加载，然后滚动到底部。</summary>
    internal async Task ShowLatestAsync()
    {
        _newWhileScrolledUp = 0;
        UpdateBottomBanner();
        if (_session.Messages.TryGet(Key) is { FoundNewest: true })
            ScrollRequested?.Invoke(this, ScrollRequest.Bottom);
        else
            await ReloadAsync();
    }

    private void MarkReadIfViewing()
    {
        if (_loaded && _session.Unread.Count(Key) > 0 && _session.Registry.IsActivelyViewing(Key))
            _session.Forget(_session.Chat.MarkConversationReadAsync(Key), "标记会话已读");
    }

    private void LoadOlder()
    {
        _loadingOlder = true;
        RebuildItems();
        _ = LoadMoreAsync(older: true);
    }

    private void LoadNewer()
    {
        _loadingNewer = true;
        _ = LoadMoreAsync(older: false);
    }

    private async Task LoadMoreAsync(bool older)
    {
        try
        {
            if (older)
                await _session.Messages.LoadOlderAsync(Key);
            else
                await _session.Messages.LoadNewerAsync(Key);
        }
        catch (Exception ex)
        {
            if (!_session.IsDisposed)
                Log.Warn($"加载消息失败：{Key}", ex);
        }
        finally
        {
            if (older)
                _loadingOlder = false;
            else
                _loadingNewer = false;
            if (!_closed && older)
                RebuildItems();
            // 向后翻到最新端：查看较早历史期间收到、保持未读的消息现在显示出来了
            if (!older && ShowsLatest)
                MarkReadIfViewing();
        }
    }

    private void RebuildItems()
    {
        var timeline = _session.Messages.TryGet(Key);
        var now = DateTime.Now;
        var desired = new List<ViewModelBase>();
        var vms = new Dictionary<long, MessageItemViewModel>();
        var separators = new Dictionary<long, TimeSeparatorItem>();
        if (_loadingOlder)
            desired.Add(_loadingItem);

        ChatMessage? previous = null;
        foreach (var m in timeline?.Messages ?? [])
        {
            if (m.IsHiddenByPlugin)
                continue;

            var needsSeparator = previous is null || TimeText.NeedsSeparator(previous.Timestamp, m.Timestamp);
            if (m.Id == _firstUnreadId)
            {
                // “新消息”分隔线后面总是跟一个时间条
                desired.Add(_divider);
                needsSeparator = true;
            }
            if (needsSeparator)
            {
                if (!_separators.TryGetValue(m.Id, out var separator))
                    separator = new TimeSeparatorItem();
                separator.Text = TimeText.SeparatorTime(TimeText.FromUnix(m.Timestamp), now);
                separators[m.Id] = separator;
                desired.Add(separator);
            }

            if (!_vms.TryGetValue(m.Id, out var vm) || vm.Message != m)
                vm = new MessageItemViewModel(this, _session, m);
            vm.IsTimeShown = m.Id == _timeShownMessageId;
            vm.IsChecked = _checked.Contains(m.Id);
            vms[m.Id] = vm;
            desired.Add(vm);
            previous = m;
        }

        _vms = vms;
        _separators = separators;
        CollectionSync.SyncTo(Items, desired);
    }

    private void UpdateTopBanner()
    {
        if (_mentionQueue.Count > 0)
        {
            TopBannerText = _mentionQueue[0].Kind == MentionKind.Personal ? "@我" : "@全体成员";
            return;
        }
        // 视图第一次报告可见范围之前不显示；未读全部在第一屏内时自然不显示
        if (_minTopSeen == long.MaxValue)
        {
            TopBannerText = null;
            return;
        }
        var above = _unreadAtOpen.Count(id => id < _minTopSeen);
        TopBannerText = above > 0 ? $"{above}条新消息" : null;
    }

    private void UpdateBottomBanner() =>
        BottomBannerText = _newWhileScrolledUp > 0 && (!_isAtBottom || !ShowsLatest) ? $"{_newWhileScrolledUp}条新消息" : null;

    private void Flash(long id)
    {
        if (!_vms.TryGetValue(id, out var vm))
            return;
        vm.IsFlashing = true;
        DispatcherTimer.RunOnce(() => vm.IsFlashing = false, FlashDuration);
    }

    private void RefreshTitle()
    {
        var title = _session.Conversations.GetTitle(Key);
        Title = Key switch
        {
            TopicKey t when _session.Channels.Get(t.StreamId) is { } channel => $"{title} ({channel.SubscriberCount})",
            DmKey { IsGroupDm: true } d => $"{title} ({d.OtherUserIds.Length + 1})",
            _ => title,
        };
    }

    private void RefreshMessages()
    {
        foreach (var vm in _vms.Values)
            vm.Refresh();
    }

    private void Subscribe()
    {
        _session.Messages.TimelineChanged += OnTimelineChanged;
        _session.Messages.MessageUpdated += OnMessageUpdated;
        _session.Resynced += OnResynced;
        _session.Conversations.EntryChanged += OnEntryChanged;
        _session.Contacts.Changed += OnNamesChanged;
        _session.Users.UserChanged += OnUserChanged;
        _session.Users.MutedChanged += OnMutedChanged;
        _session.Channels.ChannelChanged += OnChannelChanged;
    }

    private void Unsubscribe()
    {
        _session.Messages.TimelineChanged -= OnTimelineChanged;
        _session.Messages.MessageUpdated -= OnMessageUpdated;
        _session.Resynced -= OnResynced;
        _session.Conversations.EntryChanged -= OnEntryChanged;
        _session.Contacts.Changed -= OnNamesChanged;
        _session.Users.UserChanged -= OnUserChanged;
        _session.Users.MutedChanged -= OnMutedChanged;
        _session.Channels.ChannelChanged -= OnChannelChanged;
    }

    private void OnMutedChanged(object? sender, EventArgs e) => RefreshMessages();

    private void OnTimelineChanged(object? sender, (ConversationKey Key, TimelineChange Change) e)
    {
        if (!_opened || _closed || !e.Key.Equals(Key))
            return;
        switch (e.Change.Kind)
        {
            case TimelineChangeKind.Invalidated:
                _ = ReloadAsync();
                return;
            case TimelineChangeKind.Appended when !_loadingNewer:
                RebuildItems();
                // 自己发送的消息总是滚动到底部
                var last = _session.Messages.TryGet(Key)?.Messages.LastOrDefault();
                if (_isAtBottom || last?.SenderId == _session.SelfId)
                {
                    ScrollRequested?.Invoke(this, ScrollRequest.Bottom);
                }
                else
                {
                    _newWhileScrolledUp += e.Change.Count;
                    UpdateBottomBanner();
                }
                return;
            default:
                // 向后加载、回显被替换、删除等：视图保持位置；原本在最新端的底部时继续停在底部
                RebuildItems();
                if (_isAtBottom && !_loadingNewer && _session.Messages.TryGet(Key) is { FoundNewest: true })
                    ScrollRequested?.Invoke(this, ScrollRequest.Bottom);
                return;
        }
    }

    private void OnMessageUpdated(object? sender, long id)
    {
        if (_vms.TryGetValue(id, out var vm))
            vm.Refresh();
    }

    private void OnResynced(object? sender, EventArgs e) => _ = ReloadAsync();

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        if (key.Equals(Key))
            RefreshTitle();
    }

    private void OnNamesChanged(object? sender, EventArgs e)
    {
        RefreshTitle();
        RefreshMessages();
    }

    private void OnUserChanged(object? sender, long id)
    {
        RefreshTitle();
        foreach (var vm in _vms.Values.Where(v => v.Message.SenderId == id))
            vm.Refresh();
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (Key is TopicKey t && t.StreamId == streamId)
            RefreshTitle();
    }
}
