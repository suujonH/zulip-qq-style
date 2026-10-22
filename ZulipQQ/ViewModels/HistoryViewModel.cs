using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

public enum HistoryTab
{
    All,
    Media,
    Files,
    Links,
}

/// <summary>聊天记录中同一天的一组条目。图片标签页为缩略图网格，其它为列表。</summary>
public sealed class HistoryGroup(DateTime date, bool isGrid) : ViewModelBase
{
    public DateTime Date { get; } = date;

    public string Header { get; } = date.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture);

    public bool IsGrid { get; } = isGrid;

    public ObservableCollection<ViewModelBase> Entries { get; } = [];
}

/// <summary>聊天记录中的一条消息（“全部”“链接”标签页）。</summary>
public sealed partial class HistoryMessageItem(HistoryViewModel owner, MessageDto dto, string senderName, string? avatarUrl, bool isFirst, bool use24h)
    : ViewModelBase
{
    public long MessageId { get; } = dto.Id;

    public long SenderId { get; } = dto.SenderId;

    public string SenderName { get; } = senderName;

    public string? AvatarUrl { get; } = avatarUrl;

    public string TimeText { get; } = Core.TimeText.ClockTime(Core.TimeText.FromUnix(dto.Timestamp), use24h);

    /// <summary>显示的内容：搜索时为带高亮的 <c>match_content</c>。</summary>
    public string Html { get; } = dto.MatchContent ?? dto.Content;

    /// <summary>原始内容（取图片列表用）。</summary>
    public string Content { get; } = dto.Content;

    public string? MeSender { get; } = dto.IsMeMessage ? senderName : null;

    /// <summary>每组的第一条上方不画分隔线。</summary>
    public bool ShowDivider { get; } = !isFirst;

    [RelayCommand]
    private void Locate() => owner.Locate(MessageId);
}

/// <summary>聊天记录中的一张图片（“图片/视频”标签页）。</summary>
public sealed partial class HistoryImageItem(HistoryViewModel owner, long messageId, InlineImage image) : ViewModelBase
{
    public long MessageId { get; } = messageId;

    public InlineImage Image { get; } = image;

    [RelayCommand]
    private void Locate() => owner.Locate(MessageId);
}

/// <summary>聊天记录中的一个视频（“图片/视频”标签页）：点击打开（未下载时先下载）。</summary>
public sealed partial class HistoryVideoItem(HistoryViewModel owner, long messageId, UploadLink link) : ViewModelBase
{
    public long MessageId { get; } = messageId;

    public UploadLink Link { get; } = link;

    [RelayCommand]
    private void Open() => owner.OpenFile(Link, MessageId);

    [RelayCommand]
    private void Locate() => owner.Locate(MessageId);
}

/// <summary>聊天记录中的一个文件（“文件”标签页）。</summary>
public sealed partial class HistoryFileItem(HistoryViewModel owner, long messageId, UploadLink link) : ViewModelBase
{
    public long MessageId { get; } = messageId;

    public UploadLink Link { get; } = link;

    public bool IsVideo => ZulipHtml.IsVideoPath(Link.Path);

    [RelayCommand]
    private void Locate() => owner.Locate(MessageId);
}

/// <summary>
/// 聊天记录窗口：一个会话的消息，按标签页（全部 / 图片 / 文件 / 链接）、搜索词、发送者、日期筛选。
/// 最新的在上面，按日期分组；滚动到底部附近时加载更早的消息。搜索框 400ms 防抖。
/// 日期筛选（显示该日及之前的消息）需要服务器 feature level ≥ 445。
/// </summary>
public partial class HistoryViewModel : ViewModelBase
{
    public const int DateAnchorFeatureLevel = 445;
    private const int PageSize = 50;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchTimer = new() { Interval = SearchDelay };

    // 每次重新加载递增，丢弃过期的结果
    private int _version;
    private long? _oldestId;
    private bool _foundOldest;
    private bool _loading;
    private bool _closed;
    private int _toastVersion;

    public HistoryViewModel(ZulipSession session, MainViewModel main, ConversationKey key, string? query)
    {
        _session = session;
        _main = main;
        Key = key;
        Title = session.Conversations.GetTitle(key);
        Query = query ?? "";
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Reload();
        };
        session.Conversations.ConversationRekeyed += OnRekeyed;
    }

    /// <summary>话题被整体移动或改名时换成新键（参数为旧键），窗口据此更新索引。</summary>
    public event EventHandler<ConversationKey>? Rekeyed;

    public ConversationKey Key { get; private set; }

    public ZulipSession Session => _session;

    [ObservableProperty]
    public partial string Title { get; private set; }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllTab), nameof(IsMediaTab), nameof(IsFilesTab), nameof(IsLinksTab))]
    public partial HistoryTab Tab { get; private set; }

    public bool IsAllTab => Tab == HistoryTab.All;

    public bool IsMediaTab => Tab == HistoryTab.Media;

    public bool IsFilesTab => Tab == HistoryTab.Files;

    public bool IsLinksTab => Tab == HistoryTab.Links;

    /// <summary>按成员筛选：发送者 id 与显示名。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText), nameof(HasFilter))]
    public partial (long Id, string Name)? SenderFilter { get; private set; }

    /// <summary>按日期筛选：只显示这一天及之前的消息。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText), nameof(HasFilter))]
    public partial DateTime? DateFilter { get; private set; }

    public bool HasFilter => SenderFilter is not null || DateFilter is not null;

    public string FilterText => string.Join(" · ", new[]
    {
        SenderFilter is { } s ? $"成员：{s.Name}" : null,
        DateFilter is { } d ? d.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture) + " 及之前" : null,
    }.OfType<string>());

    public bool CanFilterByDate => _session.Realm.FeatureLevel >= DateAnchorFeatureLevel;

    public ObservableCollection<HistoryGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool HasError { get; private set; }

    /// <summary>列表中间的状态文字：加载中、失败、没有结果；有内容时为 null。</summary>
    public string? StatusText =>
        Groups.Count > 0 ? null
        : HasError ? "加载失败"
        : IsLoading ? "加载中…"
        : "没有找到相关的聊天记录";

    /// <summary>列表底部“加载中…”（加载更早的消息时）。</summary>
    public bool IsLoadingMore => IsLoading && Groups.Count > 0;

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    /// <summary>开始加载第一页（窗口打开后调用）。</summary>
    public void Start()
    {
        // 构造时设置的初始搜索词不需要再等防抖
        _searchTimer.Stop();
        Reload();
    }

    /// <summary>窗口关闭：停止计时器，之后的结果全部丢弃。</summary>
    public void Close()
    {
        _closed = true;
        _searchTimer.Stop();
        _version++;
        _session.Conversations.ConversationRekeyed -= OnRekeyed;
    }

    private void OnRekeyed(object? sender, (ConversationKey Old, ConversationKey New) e)
    {
        if (_closed || !Key.Equals(e.Old))
            return;
        Key = e.New;
        Title = _session.Conversations.GetTitle(e.New);
        Rekeyed?.Invoke(this, e.Old);
        Reload();
    }

    /// <summary>从搜索结果再次打开时替换搜索词。</summary>
    public void SetQuery(string query)
    {
        if (Query == query)
            return;
        Query = query;
        _searchTimer.Stop();
        Reload();
    }

    partial void OnQueryChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    [RelayCommand]
    private void SelectTab(HistoryTab tab)
    {
        if (Tab == tab)
            return;
        Tab = tab;
        Reload();
    }

    public void FilterBySender(long userId, string name)
    {
        SenderFilter = (userId, name);
        Reload();
    }

    public void FilterByDate(DateTime date)
    {
        DateFilter = date.Date;
        Reload();
    }

    [RelayCommand]
    private void ClearFilter()
    {
        SenderFilter = null;
        DateFilter = null;
        Reload();
    }

    /// <summary>“按成员”可选的成员：话题为频道订阅者，私聊为参与者（含自己）。</summary>
    public async Task<IReadOnlyList<ChatMemberItem>> GetMembersAsync()
    {
        switch (Key)
        {
            case TopicKey t:
                var ids = await _session.Channels.GetSubscribersAsync(t.StreamId);
                return ChatMembers.Build(_session, _session.Channels.Get(t.StreamId), ids);
            case DmKey d:
                return ChatMembers.Build(_session, null, [.. d.OtherUserIds, _session.SelfId]);
            default:
                return [];
        }
    }

    /// <summary>滚动到底部附近时加载更早的消息；上一页失败时再次滚动即重试。</summary>
    public void LoadOlder()
    {
        if (!_loading && !_foundOldest && _oldestId is not null)
            _ = LoadAsync(reset: false);
    }

    /// <summary>需要把主窗口带到前台（定位到聊天、打开组织内的会话链接之后）。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>定位：在聊天中打开该会话并跳转到这条消息。</summary>
    public void Locate(long messageId) => _ = OpenChatAsync(Key, messageId);

    /// <summary>在主窗口中打开会话，然后把主窗口带到前台。</summary>
    public async Task OpenChatAsync(ConversationKey key, long? messageId)
    {
        await _main.OpenChatAsync(key, messageId);
        if (!_closed)
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>已加载的全部图片（按时间先后），以及点击的那张的位置（图片查看器用）。</summary>
    public (IReadOnlyList<InlineImage> Images, int Index) CollectImages(InlineImage clicked, long? messageId)
    {
        var images = new List<(long, InlineImage)>();
        // 列表最新的在上面，图片查看器按时间先后排列
        foreach (var group in Groups.Reverse())
        {
            foreach (var entry in group.Entries.Reverse())
            {
                switch (entry)
                {
                    case HistoryImageItem image:
                        images.Add((image.MessageId, image.Image));
                        break;
                    case HistoryMessageItem message:
                        images.AddRange(ZulipHtml.ExtractInlineImages(message.Content).Select(i => (message.MessageId, i)));
                        break;
                }
            }
        }
        return MessageLinks.LocateImage(images, clicked, messageId);
    }

    /// <summary>打开文件（未下载时先下载）。</summary>
    public void OpenFile(UploadLink link, long messageId) =>
        _ = MessageLinks.HandleFileAsync(_session, new FileTarget(link), Key, messageId, null, ShowToast);

    /// <summary>在窗口中短暂显示的提示（主窗口可能被挡住）。</summary>
    public void ShowToast(string text)
    {
        var version = ++_toastVersion;
        ToastText = text;
        DispatcherTimer.RunOnce(() =>
        {
            if (version == _toastVersion)
                ToastText = null;
        }, TimeSpan.FromSeconds(2));
    }

    private void Reload()
    {
        if (!_closed)
            _ = LoadAsync(reset: true);
    }

    private async Task LoadAsync(bool reset)
    {
        if (reset)
        {
            _version++;
            Groups.Clear();
            _oldestId = null;
            _foundOldest = false;
            HasError = false;
        }
        var version = _version;
        _loading = true;
        SetLoading(true);

        var date = reset ? DateFilter : null;
        // 日期筛选：以这一天结束时为锚点向前加载
        var endOfDay = date is { } d ? new DateTimeOffset(d.AddDays(1).AddSeconds(-1)) : (DateTimeOffset?)null;
        var query = new MessageQuery(
            BuildNarrow(),
            reset ? endOfDay is null ? "newest" : "date" : _oldestId!.Value.ToString(CultureInfo.InvariantCulture),
            PageSize,
            0,
            IncludeAnchor: reset,
            AnchorDate: endOfDay?.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture));

        GetMessagesResult result;
        try
        {
            result = await _session.Client.GetMessagesAsync(query, _session.Token);
        }
        catch (Exception ex)
        {
            if (version != _version || _session.IsDisposed)
                return;
            Log.Warn($"加载聊天记录失败：{Key}", ex);
            _loading = false;
            HasError = true;
            SetLoading(false);
            // 已有内容时列表中间不显示状态文字，用提示告知；再次滚动到底部即重试
            if (Groups.Count > 0)
                ShowToast("加载失败，请稍后再试");
            return;
        }
        if (version != _version || _session.IsDisposed)
            return;

        // 锚点可能是这一天之后的第一条消息，不显示
        var limit = endOfDay?.ToUnixTimeSeconds();
        var appended = false;
        foreach (var dto in Enumerable.Reverse(result.Messages))
        {
            if ((limit is null || dto.Timestamp <= limit) && Append(dto))
                appended = true;
        }
        if (result.Messages.Count > 0)
            _oldestId = result.Messages.Min(m => m.Id);
        _foundOldest = result.FoundOldest || result.Messages.Count == 0;
        _loading = false;
        // 这一页的消息都没有对应条目（例如图片/视频标签页中只有文件的消息）：继续向前加载，
        // 否则会误显示“没有找到”，列表也没有可滚动的内容触发加载
        if (!appended && !_foundOldest)
        {
            await LoadAsync(reset: false);
            return;
        }
        SetLoading(false);
    }

    private void SetLoading(bool loading)
    {
        IsLoading = loading;
        OnPropertyChanged(nameof(IsLoadingMore));
        OnPropertyChanged(nameof(StatusText));
    }

    private List<NarrowTerm> BuildNarrow()
    {
        var topicName = Key is TopicKey t ? _session.Conversations.GetTopicName(t) : null;
        var narrow = Narrow.For(Key, _session.SelfId, topicName);
        switch (Tab)
        {
            // has:image 不包含视频，所以图片/视频与文件都按附件查询，再在客户端按类型取出条目。
            // 同时含图片和文件的消息因此两边都会出现。
            case HistoryTab.Media:
            case HistoryTab.Files:
                narrow.Add(Narrow.Has("attachment"));
                break;
            case HistoryTab.Links:
                narrow.Add(Narrow.Has("link"));
                break;
        }
        if (Query.Trim() is { Length: > 0 } q)
            narrow.Add(Narrow.Search(q));
        if (SenderFilter is { } sender)
            narrow.Add(Narrow.Sender(sender.Id));
        return narrow;
    }

    // 消息按从新到旧的顺序追加：同一天的放进最后一组。返回是否产生了条目
    private bool Append(MessageDto dto)
    {
        List<ViewModelBase> entries = Tab switch
        {
            HistoryTab.Media => MediaEntries(dto),
            HistoryTab.Files => ZulipHtml.ExtractUploads(dto.Content).Where(u => !u.IsImage && !ZulipHtml.IsVideoPath(u.Path))
                .Select(u => (ViewModelBase)new HistoryFileItem(this, dto.Id, u)).ToList(),
            _ => [MessageItem(dto)],
        };
        if (entries.Count == 0)
            return false;

        var date = TimeText.FromUnix(dto.Timestamp).Date;
        var group = Groups.LastOrDefault();
        if (group is null || group.Date != date)
        {
            group = new HistoryGroup(date, isGrid: Tab == HistoryTab.Media);
            Groups.Add(group);
            OnPropertyChanged(nameof(StatusText));
            if (Tab is HistoryTab.All or HistoryTab.Links)
                entries = [MessageItem(dto, isFirst: true)];
        }
        foreach (var entry in entries)
            group.Entries.Add(entry);
        return true;
    }

    // 图片/视频页的条目：内联预览图；没有预览的上传图片（例如写在句中的链接）直接用原图；上传的视频
    private List<ViewModelBase> MediaEntries(MessageDto dto)
    {
        var images = ZulipHtml.ExtractInlineImages(dto.Content);
        var previewed = images.Select(i => i.Original).ToHashSet();
        var uploads = ZulipHtml.ExtractUploads(dto.Content);
        return
        [
            .. images.Select(i => new HistoryImageItem(this, dto.Id, i)),
            .. uploads.Where(u => u.IsImage && !previewed.Contains(u.Path))
                .Select(u => new HistoryImageItem(this, dto.Id, new InlineImage(u.Path, u.Path))),
            .. uploads.Where(u => ZulipHtml.IsVideoPath(u.Path)).Select(u => new HistoryVideoItem(this, dto.Id, u)),
        ];
    }

    private HistoryMessageItem MessageItem(MessageDto dto, bool isFirst = false)
    {
        var user = _session.Users.Get(dto.SenderId);
        var name = user is null ? dto.SenderFullName : _session.Contacts.DisplayName(dto.SenderId);
        return new HistoryMessageItem(this, dto, name, user?.AvatarUrl ?? dto.AvatarUrl, isFirst, _session.Settings.Zulip.TwentyFourHourTime);
    }
}
