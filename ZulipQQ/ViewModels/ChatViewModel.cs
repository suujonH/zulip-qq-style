using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
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
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 一个打开的会话（主窗口右侧或独立窗口）。负责消息列表、时间分隔条、“新消息”分隔线，
/// 以及右上角的“N条新消息 / @我”和右下角的“N条新消息”按钮。滚动由视图执行。
/// </summary>
public partial class ChatViewModel : ViewModelBase
{
    private static readonly TimeSpan FlashDuration = TimeSpan.FromSeconds(1.5);

    internal const string ReadStateText = "已读";
    internal const string UnreadStateText = "未读";

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

    // 正在就地编辑的消息与编辑前的原文
    private MessageItemViewModel? _editing;
    private string _editOriginal = "";

    // 一对一私聊中自己最后一条消息的已读状态；版本号丢弃过期的查询结果
    private long _readStateId;
    private string? _readState;
    private int _readStateVersion;

    // “从这里标为未读”之后，本次打开期间不再自动标记已读（与网页端一致）
    private bool _autoReadSuspended;

    // 正在输入的用户与其状态的过期时间（UTC）；超过 started_expiry 没有收到新的 start 即视为停止
    private readonly Dictionary<long, DateTime> _typers = [];
    private DispatcherTimer? _typingTimer;

    public ChatViewModel(ZulipSession session, MainViewModel main, ConversationKey key)
    {
        _session = session;
        Main = main;
        _openChat = (k, f) => main.OpenChatAsync(k, f);
        Host = main;
        _showToast = text => Host.ShowToast(text);
        Key = key;
        Composer = new ComposerViewModel(this, session, _showToast);
        Sidebar = key is TopicKey ? new TopicSidebarViewModel(this) : null;
        RefreshTitle();
    }

    public MainViewModel Main { get; }

    /// <summary>显示对话框和提示的窗口：在主窗口右侧时为主窗口，移入独立聊天窗口后为该窗口。</summary>
    public IDialogHost Host { get; set; }

    /// <summary>聊天在独立聊天窗口中打开。</summary>
    public bool IsStandalone { get; set; }

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

    /// <summary>话题（标题栏显示“群文件”按钮）；话题被移动后仍是话题。</summary>
    public bool IsTopic => Key is TopicKey;

    public ZulipSession Session => _session;

    /// <summary>话题和多人私聊显示发送者昵称。</summary>
    public bool ShowsSenderNames => Key is TopicKey or DmKey { IsGroupDm: true };

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>标题下方的“对方正在输入…”；没有人在输入时为 null。</summary>
    [ObservableProperty]
    public partial string? TypingText { get; private set; }

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

    /// <summary>开始就地编辑一条消息：视图把焦点放到它的输入框。</summary>
    public event EventHandler<MessageItemViewModel>? EditStarted;

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
        _ = RefreshReadStateAsync();
    }

    /// <summary>关闭会话（幂等）：取消订阅、从打开列表中移除，并等待草稿保存完成。</summary>
    public Task CloseAsync()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        ClearTyping();
        CloseMorePanel();
        ExitMultiSelect();
        Unsubscribe();
        _session.Registry.Unregister(this);
        var saving = Composer.CloseAsync();
        _session.Registry.TrackClosing(saving);
        return saving;
    }

    /// <summary>已经关闭（或正在关闭）：不能再交给独立窗口使用。</summary>
    public bool IsClosed => _closed;

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
        _ = RefreshReadStateAsync();
    }

    /// <summary>话题被整体移动或改名：换成新键并重新加载。</summary>
    public void Rekey(ConversationKey newKey)
    {
        var oldKey = Key;
        ExitMultiSelect();
        ClearTyping();
        Key = newKey;
        RefreshTitle();
        Composer.OnKeyChanged(oldKey);
        _ = ReloadAsync();
    }

    /// <summary>正在查看时自动标记已读；“从这里标为未读”之后为 false。</summary>
    public bool AutoMarksRead => !_autoReadSuspended;

    /// <summary>新消息会出现在列表末尾：消息已加载，且当前窗口包含最新的消息。</summary>
    public bool ShowsLatest => _loaded && !_closed && _session.Messages.TryGet(Key) is { FoundNewest: true };

    /// <summary>
    /// 本会话收到新消息（在 MessageStore 处理之后调用）。正在查看较早的历史时消息不会追加到列表，
    /// 这里计入右下角的“N条新消息”。
    /// </summary>
    public void OnIncomingMessage(ChatMessage m)
    {
        // 对方发出了消息：不再显示正在输入
        if (_typers.Remove(m.SenderId))
            RefreshTyping();
        if (!_opened || _closed || m.SenderId == _session.SelfId || ShowsLatest)
            return;
        _newWhileScrolledUp++;
        UpdateBottomBanner();
    }

    /// <summary>本会话的 typing 事件。</summary>
    public void OnTyping(long userId, bool started)
    {
        if (_closed)
            return;
        if (started)
            _typers[userId] = DateTime.UtcNow.AddMilliseconds(_session.Realm.TypingStartedExpiryMs);
        else if (!_typers.Remove(userId))
            return;
        RefreshTyping();
    }

    // 一对一私聊显示“对方正在输入…”；其它会话显示名字（超过 3 人时显示“多人正在输入…”）
    private void RefreshTyping()
    {
        var now = DateTime.UtcNow;
        foreach (var id in _typers.Where(p => p.Value <= now).Select(p => p.Key).ToList())
            _typers.Remove(id);
        if (_typers.Count == 0)
        {
            TypingText = null;
            _typingTimer?.Stop();
            return;
        }
        TypingText = Key is DmKey { OtherUserIds.Length: 1 } ? "对方正在输入…"
            : _typers.Count > 3 ? "多人正在输入…"
            : string.Join("、", _typers.Keys.Select(_session.Contacts.DisplayName)) + " 正在输入…";
        if (_typingTimer is null)
        {
            _typingTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _typingTimer.Tick += (_, _) => RefreshTyping();
        }
        _typingTimer.Start();
    }

    private void ClearTyping()
    {
        _typers.Clear();
        _typingTimer?.Stop();
        TypingText = null;
    }

    /// <summary>所在窗口被激活：补标已读（打开时窗口不在前台，或在后台收到了新消息）。</summary>
    public void OnHostActivated()
    {
        if (!_snapshotTaken || _closed)
            return;
        MarkReadIfViewing();
        // 对方可能在此期间看过了消息（已读没有事件通知）
        if (_opened)
            _ = RefreshReadStateAsync();
    }

    /// <summary>
    /// 一对一私聊（组织开启已读回执时）：查询自己最后一条消息是否已被对方读过，显示在它下方。
    /// 对方关闭了已读回执时服务器不返回对方，始终显示为未读。只在已加载到最新的消息时更新。
    /// </summary>
    private async Task RefreshReadStateAsync()
    {
        if (_closed || !_session.Realm.EnableReadReceipts || Key is not DmKey { OtherUserIds.Length: 1 } dm)
        {
            SetReadState(0, null);
            return;
        }
        if (_session.Messages.TryGet(Key) is not { FoundNewest: true } timeline)
            return;
        var last = timeline.Messages.LastOrDefault(m => m.SenderId == _session.SelfId && m.Id > 0 && !m.IsHiddenByPlugin);
        if (last is null)
        {
            SetReadState(0, null);
            return;
        }

        var version = ++_readStateVersion;
        IReadOnlyList<long> readers;
        try
        {
            readers = await _session.Client.GetReadReceiptsAsync(last.Id, _session.Token);
        }
        catch (Exception ex)
        {
            if (!_session.IsDisposed)
                Log.Debug($"查询已读状态失败：{last.Id}", ex);
            return;
        }
        if (version != _readStateVersion || _closed || !Key.Equals(dm))
            return;
        SetReadState(last.Id, readers.Contains(dm.OtherUserIds[0]) ? ReadStateText : UnreadStateText);
    }

    private void SetReadState(long id, string? text)
    {
        if (_readStateId != id && _vms.TryGetValue(_readStateId, out var previous))
            previous.ReadStateText = null;
        _readStateId = id;
        _readState = text;
        if (_vms.TryGetValue(id, out var vm))
            vm.ReadStateText = text;
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

    // ---- 聊天中的用户（消息的头像和昵称、群聊成员）：双击发消息，右键菜单 ----

    /// <summary>用户是否存在（已被删除的用户没有菜单，双击也不打开私聊）。</summary>
    public bool HasUser(long userId) => _session.Users.Get(userId) is not null;

    /// <summary>打开与该用户的私聊；是自己时打开与自己的私聊。</summary>
    public void OpenDirectChat(long userId)
    {
        if (HasUser(userId))
            _ = _openChat(DmKey.Create([userId], _session.SelfId), null);
    }

    /// <summary>可以 @TA：不是自己，并且输入框可以编辑。</summary>
    public bool CanMention(long userId) => userId != _session.SelfId && HasUser(userId) && Composer.CanEdit;

    public void Mention(long userId)
    {
        if (CanMention(userId))
            Composer.Mention(userId, _session.Users.Get(userId)!.FullName);
    }

    /// <summary>可以屏蔽（Zulip 的静音用户）：不能屏蔽自己。</summary>
    public bool CanMuteUser(long userId) => userId != _session.SelfId && HasUser(userId);

    public bool IsUserMuted(long userId) => _session.Users.IsMuted(userId);

    /// <summary>屏蔽此人（先确认）/ 取消屏蔽。取消确认时返回；失败时提示。结果由 muted_users 事件更新。</summary>
    public async Task SetUserMutedAsync(long userId, bool muted)
    {
        try
        {
            if (muted && !await Host.ConfirmAsync("屏蔽后将不再显示对方的消息", "屏蔽"))
                return;
            if (muted)
                await _session.Client.MuteUserAsync(userId, _session.Token);
            else
                await _session.Client.UnmuteUserAsync(userId, _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"设置屏蔽失败：{userId}", ex);
            _showToast("设置失败");
        }
    }

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
    /// 同一张图片可能出现在多条消息中，优先匹配点击所在的消息 <paramref name="messageId"/>。
    /// 点击的图片不在消息中（例如群公告里的图片）时只有它一张。
    /// </summary>
    public (IReadOnlyList<InlineImage> Images, int Index) CollectImages(ImageTarget clicked, long? messageId)
    {
        var images = new List<(long MessageId, InlineImage Image)>();
        foreach (var m in _session.Messages.TryGet(Key)?.Messages ?? [])
        {
            if (!m.IsHiddenByPlugin && m.Id > 0 && !(m.SenderId != _session.SelfId && _session.Users.IsMuted(m.SenderId)))
                images.AddRange(ZulipHtml.ExtractInlineImages(m.ContentHtml).Select(i => (m.Id, i)));
        }
        return MessageLinks.LocateImage(images, new InlineImage(clicked.Src, clicked.Href), messageId);
    }

    /// <summary>用浏览器打开消息（不走客户端内部跳转）。</summary>
    public void OpenInBrowser(MessageItemViewModel item)
    {
        var url = MessageLink(item.Id);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败：{url}", ex);
        }
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

        ForwardDialogViewModel.Show(Main, Host, preview, BuildAsync);
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

    // ---- 编辑、编辑历史、已读成员、标为未读、提醒 ----

    /// <summary>可以编辑：自己的消息，组织允许编辑，且在编辑时限内。</summary>
    // 投票 / 待办消息的内容不能编辑（与网页端一致）
    public bool CanEdit(MessageItemViewModel item) =>
        item.Id > 0 && item.Widget is null && _session.Permissions.CanEditMessage(item.Message.SenderId, item.Message.Timestamp,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    /// <summary>就地编辑：取 Markdown 原文后气泡换成输入框。同一时间只编辑一条。</summary>
    public async Task BeginEditAsync(MessageItemViewModel item)
    {
        string raw;
        try
        {
            raw = await _session.Chat.GetRawContentAsync(item.Id);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"获取消息原文失败：{item.Id}", ex);
            _showToast("获取消息原文失败");
            return;
        }
        if (_closed)
            return;
        if (_editing is { } previous)
            CancelEdit(previous);
        _editing = item;
        _editOriginal = raw;
        item.EditText = raw;
        item.IsEditing = true;
        EditStarted?.Invoke(this, item);
    }

    public void CancelEdit(MessageItemViewModel item)
    {
        item.IsEditing = false;
        if (_editing == item)
            _editing = null;
    }

    /// <summary>保存编辑：内容为空或过长时提示；没有修改时直接结束编辑。新内容由 update_message 事件显示。</summary>
    public async Task SaveEditAsync(MessageItemViewModel item)
    {
        if (!item.IsEditing || item.IsSavingEdit)
            return;
        var text = item.EditText.TrimEnd();
        if (string.IsNullOrWhiteSpace(text))
        {
            _showToast("消息内容不能为空");
            return;
        }
        if (_session.Realm.MaxMessageLength > 0 && text.Length > _session.Realm.MaxMessageLength)
        {
            _showToast("消息过长");
            return;
        }
        if (text == _editOriginal.TrimEnd())
        {
            CancelEdit(item);
            return;
        }

        item.IsSavingEdit = true;
        try
        {
            await _session.Chat.EditAsync(item.Id, text);
            CancelEdit(item);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"编辑消息失败：{item.Id}", ex);
            _showToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"编辑失败：{api.Message}" : "编辑失败");
        }
        finally
        {
            item.IsSavingEdit = false;
        }
    }

    /// <summary>编辑过的消息，且组织允许查看编辑历史。</summary>
    public bool CanShowEditHistory(MessageItemViewModel item) =>
        item.IsEdited && _session.Realm.EditHistoryVisibilityPolicy != "none";

    public void ShowEditHistory(MessageItemViewModel item) =>
        EditHistoryDialogViewModel.Show(_session, Host, item.Id, (target, top) => HandleLinkAsync(item, target, top));

    /// <summary>话题和多人私聊，且组织开启了已读回执。</summary>
    public bool CanShowReadReceipts => _session.Realm.EnableReadReceipts && Key is TopicKey or DmKey { IsGroupDm: true };

    public void ShowReadReceipts(MessageItemViewModel item) =>
        ReadReceiptsDialogViewModel.Show(_session, Host, item.Id, item.Message.SenderId);

    /// <summary>从这条消息开始（含）全部标为未读；之后本次打开期间不再自动标记已读。</summary>
    public async Task MarkUnreadFromAsync(MessageItemViewModel item)
    {
        _autoReadSuspended = true;
        try
        {
            await _session.Chat.MarkUnreadFromAsync(Key, item.Id);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"标为未读失败：{item.Id}", ex);
            _showToast("操作失败");
        }
    }

    /// <summary>在 <paramref name="at"/>（本地时间）提醒自己这条消息；到时服务器发一条私信给自己。</summary>
    public async Task RemindAsync(MessageItemViewModel item, DateTime at)
    {
        try
        {
            await _session.Chat.CreateReminderAsync(item.Id, new DateTimeOffset(at).ToUnixTimeSeconds());
            _showToast($"将在 {at:yyyy/MM/dd HH:mm} 提醒你");
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"设置提醒失败：{item.Id}", ex);
            _showToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"设置提醒失败：{api.Message}" : "设置提醒失败");
        }
    }

    /// <summary>自定义提醒时间。</summary>
    public async Task RemindCustomAsync(MessageItemViewModel item)
    {
        if (await DateTimeDialogViewModel.ShowAsync(Host, "提醒我", "设置提醒") is { } at && !_closed)
            await RemindAsync(item, at);
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
        if (_loaded && !_autoReadSuspended && _session.Unread.Count(Key) > 0 && _session.Registry.IsActivelyViewing(Key))
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
                separator.Text = TimeText.SeparatorTime(TimeText.FromUnix(m.Timestamp), now, _session.Settings.Zulip.TwentyFourHourTime);
                separators[m.Id] = separator;
                desired.Add(separator);
            }

            if (!_vms.TryGetValue(m.Id, out var vm) || vm.Message != m)
                vm = new MessageItemViewModel(this, _session, m);
            vm.IsTimeShown = m.Id == _timeShownMessageId;
            vm.IsChecked = _checked.Contains(m.Id);
            vm.ReadStateText = m.Id == _readStateId ? _readState : null;
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
        _session.Settings.Changed += OnSettingChanged;
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
        _session.Settings.Changed -= OnSettingChanged;
    }

    private void OnMutedChanged(object? sender, EventArgs e) => RefreshMessages();

    // 12 / 24 小时制变化：时间条和精确时间重新格式化
    private void OnSettingChanged(object? sender, string name)
    {
        if (name is not ("" or "twenty_four_hour_time") || !_opened || _closed)
            return;
        RebuildItems();
        foreach (var vm in _vms.Values)
            vm.RefreshTimeText();
    }

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
                _ = RefreshReadStateAsync();
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
                if (e.Change.Kind is TimelineChangeKind.Replaced or TimelineChangeKind.Removed)
                    _ = RefreshReadStateAsync();
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
