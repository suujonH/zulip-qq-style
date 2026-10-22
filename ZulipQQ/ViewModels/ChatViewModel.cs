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

    public ChatViewModel(ZulipSession session, ConversationKey key, Func<ConversationKey, long?, Task> openChat, Action<string> showToast)
    {
        _session = session;
        _openChat = openChat;
        _showToast = showToast;
        Key = key;
        RefreshTitle();
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

    /// <summary>关闭会话（幂等）：取消订阅并从打开列表中移除。</summary>
    public Task CloseAsync()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        Unsubscribe();
        _session.Registry.Unregister(this);
        return Task.CompletedTask;
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
        Key = newKey;
        RefreshTitle();
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

    /// <summary>处理消息内容中的点击：链接、文件、图片。</summary>
    public async Task HandleLinkAsync(MessageItemViewModel? item, MessageLinkTarget target, TopLevel? top)
    {
        var messageId = item?.Id ?? 0;
        switch (target)
        {
            case UrlTarget url:
                await OpenUrlAsync(url.Href);
                break;
            case FileTarget { Action: FileAction.Open } file:
                if (!await _session.Files.OpenAsync(file.Link, Key, messageId) && !_session.IsDisposed)
                    _showToast("文件下载失败");
                break;
            case FileTarget { Action: FileAction.SaveAs } file when top is not null:
                if (!await _session.Files.SaveAsAsync(file.Link, top) && !_session.IsDisposed)
                    _showToast("文件下载失败");
                break;
            case FileTarget { Action: FileAction.RevealInFolder } file:
                if (_session.Files.LocalPath(file.Link.Path) is { } path)
                    FileService.RevealInFolder(path);
                break;
            case ImageTarget image:
                // 图片查看器在后续分支实现，目前上传的图片下载后用系统程序打开
                if (ZulipHtml.NormalizeUploadPath(image.Href) is { } upload)
                {
                    var name = Uri.UnescapeDataString(upload[(upload.LastIndexOf('/') + 1)..]);
                    if (!await _session.Files.OpenAsync(new UploadLink(upload, name, true), Key, messageId) && !_session.IsDisposed)
                        _showToast("图片下载失败");
                }
                else
                {
                    await OpenUrlAsync(image.Href);
                }
                break;
            // 提及的用户名片在联系人分支实现
        }
    }

    internal async Task ToggleReactionAsync(MessageItemViewModel item, ReactionViewModel reaction)
    {
        if (item.Id <= 0)
            return;
        try
        {
            await _session.Chat.ToggleReactionAsync(item.Message, reaction.Name, reaction.Code, reaction.Type);
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
    private async Task BottomBannerClickAsync()
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

    private async Task OpenUrlAsync(string href)
    {
        var realm = _session.Client.RealmUrl;
        if (ZulipLinks.TryParse(href, realm, out var link))
        {
            ConversationKey? key = link switch
            {
                { StreamId: { } streamId, Topic: { } topic } => new TopicKey(streamId, topic),
                { DmUserIds: { } ids } => DmKey.Create(ids, _session.SelfId),
                _ => null,
            };
            if (key is not null)
            {
                await _openChat(key, link.NearMessageId);
                return;
            }
        }

        // 只用浏览器打开网页和邮件链接；相对链接以组织地址为基准
        if (!Uri.TryCreate(realm, href, out var uri) || uri.Scheme is not ("http" or "https" or "mailto"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败：{uri}", ex);
        }
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
        _session.Channels.ChannelChanged -= OnChannelChanged;
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
