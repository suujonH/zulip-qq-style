using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
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

public enum FilesView
{
    /// <summary>全部文件（所有会话中的附件）。</summary>
    All,

    /// <summary>最近使用：本地下载记录，按最近打开或下载时间。</summary>
    Recent,

    /// <summary>发送人：中间栏选择用户。</summary>
    Sender,

    /// <summary>会话：中间栏选择消息列表中的会话。</summary>
    Conversation,

    /// <summary>群文件：中间栏选择话题，可上传。</summary>
    Group,
}

public enum FileSort
{
    Time,
    Name,
    Size,
}

/// <summary>文件列表中的一个文件。大小由窗口按顺序懒加载；下载状态随 <see cref="FileService.StateChanged"/> 刷新。</summary>
public sealed partial class FileEntry : ViewModelBase
{
    private readonly FilesViewModel _owner;

    public FileEntry(FilesViewModel owner, UploadLink link, long messageId, ConversationKey key, long timestamp,
        string sourceText, long? size, DateTime now, bool use24h)
    {
        _owner = owner;
        Link = link;
        MessageId = messageId;
        Key = key;
        Timestamp = timestamp;
        SourceText = sourceText;
        Size = size;
        var time = Core.TimeText.FromUnix(timestamp);
        TimeText = Core.TimeText.ListTime(time, now, use24h);
        GroupHeader = Core.TimeText.MonthGroup(time, now);
    }

    public UploadLink Link { get; }

    public string Path => Link.Path;

    public string FileName => Link.FileName;

    public FileKind Kind => FileKinds.Of(Link.Path);

    public bool IsImage => Kind == FileKind.Image;

    /// <summary>缩略图地址；非图片为 null，避免隐藏的图片控件把整个文件下载下来。</summary>
    public string? ImagePath => IsImage ? Link.Path : null;

    public string IconKey => FileKinds.IconKey(Link.Path);

    public long MessageId { get; }

    public ConversationKey Key { get; }

    public long Timestamp { get; }

    public string TimeText { get; }

    /// <summary>按时间排序时所在分组的标题（今天 / M月 / yyyy年M月）。</summary>
    public string GroupHeader { get; }

    /// <summary>“来自 发送人 | 会话”或“发给 会话”。</summary>
    public string SourceText { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial long? Size { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial string StateText { get; private set; } = "未下载";

    [ObservableProperty]
    public partial bool IsDownloaded { get; private set; }

    /// <summary>“大小 · 已下载 / 未下载”；大小未知时只显示状态。</summary>
    public string StatusText => Size is { } size ? $"{Controls.FileCard.FormatSize(size)} · {StateText}" : StateText;

    public void RefreshState(FileService files)
    {
        var state = files.GetState(Path);
        IsDownloaded = state == DownloadState.Downloaded;
        StateText = state switch
        {
            DownloadState.Downloading => files.GetProgress(Path) is { } p ? $"下载中 {p}%" : "下载中",
            DownloadState.Downloaded => "已下载",
            _ => "未下载",
        };
    }

    [RelayCommand]
    private void Open() => _owner.Open(this);

    [RelayCommand]
    private void SaveAs() => _owner.SaveAs(this);

    [RelayCommand]
    private void Reveal() => _owner.Reveal(this);

    [RelayCommand]
    private void Forward() => _owner.Forward(this);

    [RelayCommand]
    private void Locate() => _owner.Locate(this);
}

/// <summary>文件列表的一组：按时间排序时按月分组，其它排序只有一组且不显示标题。</summary>
public sealed class FileGroup(string? header) : ViewModelBase
{
    public string? Header { get; } = header;

    public bool HasHeader => Header is not null;

    public ObservableCollection<FileEntry> Entries { get; } = [];
}

/// <summary>中间栏的一项：发送人（用户）或会话。</summary>
public sealed partial class FileSourceItem(long? userId, ConversationKey? key, string title, string? avatarUrl, string avatarText,
    IBrush? avatarFill) : ViewModelBase
{
    public long? UserId { get; } = userId;

    public ConversationKey? Key { get; } = key;

    public string Title { get; } = title;

    public string? AvatarUrl { get; } = avatarUrl;

    public string AvatarText { get; } = avatarText;

    public IBrush? AvatarFill { get; } = avatarFill;

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 文件窗口。数据来源：
/// <list type="bullet">
/// <item>全部文件 / 发送人 / 会话 / 群文件：<c>has:attachment</c>（加上发送人或会话条件、搜索词）的消息，每页 100 条，
/// 展开为其中的上传文件，按路径去重，保留最新的一次出现；滚动到底部附近时继续加载。</item>
/// <item>最近使用：本地下载记录（最多 200 条），搜索词在本地按文件名过滤。</item>
/// </list>
/// “筛选”按扩展名在客户端过滤；群文件可按时间、文件名、大小排序，并可上传文件发到该话题。
/// </summary>
public partial class FilesViewModel : ViewModelBase
{
    private const int PageSize = 100;
    private const int RecentLimit = 200;
    private const int SenderRankingSize = 200;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchTimer = new() { Interval = SearchDelay };

    // 当前来源已加载的文件（从新到旧），按路径去重
    private readonly List<FileEntry> _entries = [];
    private readonly HashSet<string> _paths = new(StringComparer.Ordinal);
    private readonly Queue<FileEntry> _sizeQueue = new();

    private int _version;
    private int _sourcesVersion;
    private long? _oldestId;
    private bool _foundOldest;
    private bool _loading;
    private bool _closed;
    private bool _pumpingSizes;
    private int _toastVersion;

    public FilesViewModel(ZulipSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            OnQueryApplied();
        };
        session.Files.StateChanged += OnFileStateChanged;
        session.Resynced += OnResynced;
        session.Conversations.ConversationRekeyed += OnRekeyed;
    }

    public ZulipSession Session => _session;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAllView), nameof(IsRecentView), nameof(IsSenderView), nameof(IsConversationView),
        nameof(IsGroupView), nameof(ShowSources), nameof(ShowFilter), nameof(Title), nameof(CanUpload))]
    public partial FilesView View { get; private set; }

    public bool IsAllView => View == FilesView.All;

    public bool IsRecentView => View == FilesView.Recent;

    public bool IsSenderView => View == FilesView.Sender;

    public bool IsConversationView => View == FilesView.Conversation;

    public bool IsGroupView => View == FilesView.Group;

    /// <summary>中间栏（发送人、会话、群文件）。</summary>
    public bool ShowSources => View is FilesView.Sender or FilesView.Conversation or FilesView.Group;

    /// <summary>群文件显示排序下拉，其它显示筛选下拉。</summary>
    public bool ShowFilter => View != FilesView.Group;

    public ObservableCollection<FileSourceItem> Sources { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(CanUpload))]
    public partial FileSourceItem? SelectedSource { get; private set; }

    public string Title => View switch
    {
        FilesView.Recent => "最近使用",
        FilesView.Sender => SelectedSource is { } s ? $"发送人 - {s.Title}" : "发送人",
        FilesView.Conversation => SelectedSource is { } c ? $"会话 - {c.Title}" : "会话",
        FilesView.Group => SelectedSource is { } g ? $"群文件 - {g.Title}" : "群文件",
        _ => "全部文件",
    };

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>按类型筛选；null 为全部。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FilterText))]
    public partial FileKind? Filter { get; private set; }

    public string FilterText => Filter is { } kind ? KindName(kind) : "筛选";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SortText))]
    public partial FileSort Sort { get; private set; }

    public string SortText => SortName(Sort);

    public ObservableCollection<FileGroup> Groups { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsLoadingMore))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool HasError { get; private set; }

    public string? StatusText =>
        Groups.Count > 0 ? null
        : ShowSources && SelectedSource is null ? View switch
        {
            FilesView.Sender => "请选择发送人",
            FilesView.Group => "请选择群聊",
            _ => "请选择会话",
        }
        : HasError ? "加载失败"
        : IsLoading ? "加载中…"
        : Query.Trim().Length > 0 || Filter is not null ? "未找到相关文件"
        : "暂无文件";

    public bool IsLoadingMore => IsLoading && Groups.Count > 0;

    /// <summary>群文件底部的“共 N 个文件”（已加载的）。</summary>
    [ObservableProperty]
    public partial string FooterText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUpload))]
    public partial bool IsUploading { get; private set; }

    /// <summary>群文件中选中的话题可以发言时可上传。</summary>
    public bool CanUpload => View == FilesView.Group && !IsUploading && SelectedSource?.Key is TopicKey t
                             && _session.Channels.Get(t.StreamId) is { IsSubscribed: true, IsArchived: false } channel
                             && _session.Permissions.CanSendToChannel(channel);

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    public event EventHandler? MainWindowRequested;

    /// <summary>需要选择保存位置（由窗口执行另存为）。</summary>
    public event EventHandler<FileEntry>? SaveAsRequested;

    /// <summary>打开窗口或从入口再次打开时切换视图；<paramref name="key"/> 为要选中的会话（会话、群文件视图）。</summary>
    public void Navigate(FilesView view, ConversationKey? key = null)
    {
        View = view;
        // 群文件不显示筛选下拉，不能留着其它视图的筛选
        if (view == FilesView.Group)
            Filter = null;
        SelectedSource = null;
        BuildSources(key);
        Reload();
    }

    public void Close()
    {
        _closed = true;
        _searchTimer.Stop();
        _version++;
        _sizeQueue.Clear();
        _session.Files.StateChanged -= OnFileStateChanged;
        _session.Resynced -= OnResynced;
        _session.Conversations.ConversationRekeyed -= OnRekeyed;
    }

    [RelayCommand]
    private void SelectView(FilesView view)
    {
        if (View != view)
            Navigate(view);
    }

    [RelayCommand]
    private void SelectSource(FileSourceItem source)
    {
        if (SelectedSource == source)
            return;
        SetSelected(source);
        Reload();
    }

    public void SetFilter(FileKind? kind)
    {
        if (Filter == kind)
            return;
        Filter = kind;
        Rebuild();
        ContinueIfHidden();
    }

    public void SetSort(FileSort sort)
    {
        if (Sort == sort)
            return;
        Sort = sort;
        Rebuild();
    }

    [RelayCommand]
    private void Refresh() => Reload();

    partial void OnQueryChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    /// <summary>滚动到底部附近时加载更早的文件。</summary>
    public void LoadOlder()
    {
        if (View != FilesView.Recent && !_loading && !_foundOldest && _oldestId is not null)
            _ = LoadAsync(reset: false);
    }

    public void Open(FileEntry entry) => _ = OpenAsync(entry);

    private async Task OpenAsync(FileEntry entry)
    {
        if (!await _session.Files.OpenAsync(entry.Link, entry.Key, entry.MessageId) && !_session.IsDisposed && !_closed)
            ShowToast("文件下载失败");
    }

    public void SaveAs(FileEntry entry) => SaveAsRequested?.Invoke(this, entry);

    public void Reveal(FileEntry entry)
    {
        if (_session.Files.LocalPath(entry.Path) is { } path)
            FileService.RevealInFolder(path);
    }

    /// <summary>转发：重新发送文件链接。对话框显示在主窗口中，先把主窗口带到前台。</summary>
    public void Forward(FileEntry entry)
    {
        var markdown = ComposerViewModel.LinkMarkdown(entry.FileName, entry.Path);
        MainWindowRequested?.Invoke(this, EventArgs.Empty);
        ForwardDialogViewModel.Show(_main, _main, $"[文件]{entry.FileName}", () => Task.FromResult<IReadOnlyList<string>>([markdown]));
    }

    public void Locate(FileEntry entry) => _ = LocateAsync(entry);

    private async Task LocateAsync(FileEntry entry)
    {
        await _main.OpenChatAsync(entry.Key, entry.MessageId);
        if (!_closed)
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>群文件：上传本地文件并作为消息发到选中的话题，然后刷新列表。</summary>
    public async Task UploadAsync(string path)
    {
        if (!CanUpload || SelectedSource?.Key is not TopicKey key || !File.Exists(path))
            return;
        var max = _session.Realm.MaxFileUploadSizeMib;
        if (max > 0 && new FileInfo(path).Length > max * 1024L * 1024L)
        {
            ShowToast($"文件超过 {max}MB，无法上传");
            return;
        }

        IsUploading = true;
        try
        {
            var result = await _session.Chat.UploadAsync(path);
            if (_closed || _session.IsDisposed)
                return;
            var name = string.IsNullOrEmpty(result.Filename) ? Path.GetFileName(path) : result.Filename;
            if (!await _session.Chat.SendAsync(key, ComposerViewModel.LinkMarkdown(name, result.Url)))
            {
                if (!_closed)
                    ShowToast("发送失败");
                return;
            }
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed || _closed)
                return;
            Log.Warn($"上传失败：{path}", ex);
            ShowToast("上传失败");
            return;
        }
        finally
        {
            IsUploading = false;
        }
        if (_closed)
            return;
        ShowToast("已上传");
        if (SelectedSource?.Key?.Equals(key) == true)
            Reload();
    }

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

    public static string KindName(FileKind kind) => kind switch
    {
        FileKind.Image => "图片",
        FileKind.Video => "视频",
        FileKind.Document => "文档",
        FileKind.Archive => "压缩包",
        _ => "其他",
    };

    public static string SortName(FileSort sort) => sort switch
    {
        FileSort.Name => "按文件名",
        FileSort.Size => "按大小",
        _ => "按最新时间",
    };

    private void OnQueryApplied()
    {
        // 最近使用在本地按文件名过滤，其它视图带搜索词重新查询
        if (View == FilesView.Recent)
            Rebuild();
        else
            Reload();
    }

    private void OnFileStateChanged(object? sender, string path)
    {
        foreach (var entry in _entries.Where(e => e.Path == path))
            entry.RefreshState(_session.Files);
    }

    private void OnResynced(object? sender, EventArgs e)
    {
        if (!_closed)
            Reload();
    }

    // 话题被整体移动或改名：中间栏和已加载的文件都换成新键，选中的话题跟着换
    private void OnRekeyed(object? sender, (ConversationKey Old, ConversationKey New) e)
    {
        if (_closed)
            return;
        if (Sources.Any(s => e.Old.Equals(s.Key)))
        {
            var select = SelectedSource?.Key;
            Navigate(View, e.Old.Equals(select) ? e.New : select);
        }
        else if (_entries.Any(f => f.Key.Equals(e.Old)))
            Reload();
    }

    private void SetSelected(FileSourceItem? source)
    {
        if (SelectedSource is { } old)
            old.IsSelected = false;
        SelectedSource = source;
        if (source is not null)
            source.IsSelected = true;
    }

    // 中间栏：发送人先按名称排列，再按最近发送文件的先后调整；会话为消息列表顺序，群文件只取话题
    private void BuildSources(ConversationKey? select)
    {
        Sources.Clear();
        switch (View)
        {
            case FilesView.Sender:
                foreach (var user in _session.Users.ActiveHumans(excludeSelf: false).Concat(_session.Users.ActiveBots())
                             .OrderBy(u => u.Id != _session.SelfId)
                             .ThenBy(u => _session.Contacts.DisplayName(u.Id), StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), false)))
                    Sources.Add(UserSource(user.Id));
                _ = RankSendersAsync(++_sourcesVersion);
                break;
            case FilesView.Conversation:
            case FilesView.Group:
                var keys = _session.Conversations.Entries
                    .Where(e => e.InList && e.Key is not HelperKey && (View == FilesView.Conversation || e.Key is TopicKey))
                    .OrderByDescending(e => e.IsPinned)
                    .ThenByDescending(e => e.PinnedAt ?? 0)
                    .ThenByDescending(e => e.LastMessageTime)
                    .Select(e => e.Key)
                    .ToList();
                if (select is not null && !keys.Contains(select) && (View == FilesView.Conversation || select is TopicKey))
                    keys.Insert(0, select);
                foreach (var key in keys)
                    Sources.Add(ConversationSource(key));
                break;
        }
        if (select is not null)
            SetSelected(Sources.FirstOrDefault(s => select.Equals(s.Key)));
    }

    private FileSourceItem UserSource(long userId)
    {
        var name = userId == _session.SelfId ? "我" : _session.Contacts.DisplayName(userId);
        return new FileSourceItem(userId, null, name, _session.Users.Get(userId)?.AvatarUrl, name, null);
    }

    private FileSourceItem ConversationSource(ConversationKey key)
    {
        var title = _session.Conversations.GetTitle(key);
        var (url, text, fill) = ConversationAvatars.Of(_session, key, title);
        return new FileSourceItem(null, key, title, url, text, fill);
    }

    // 最近发过文件的发送人排在前面（按最近一次的先后），其余保持名称顺序
    private async Task RankSendersAsync(int version)
    {
        GetMessagesResult result;
        try
        {
            result = await _session.Client.GetMessagesAsync(
                new MessageQuery([Narrow.Has("attachment")], "newest", SenderRankingSize, 0), _session.Token);
        }
        catch (Exception ex)
        {
            if (!_session.IsDisposed)
                Log.Debug("取得发送人排序失败", ex);
            return;
        }
        if (version != _sourcesVersion || _closed || View != FilesView.Sender)
            return;
        var ranked = Enumerable.Reverse(result.Messages).Select(m => m.SenderId).Distinct().ToList();
        var order = Sources.OrderBy(s =>
        {
            var i = ranked.IndexOf(s.UserId ?? 0);
            return i >= 0 ? i : int.MaxValue;
        }).ToList();
        CollectionSync.SyncTo(Sources, order);
    }

    private void Reload()
    {
        if (_closed)
            return;
        _version++;
        _entries.Clear();
        _paths.Clear();
        _sizeQueue.Clear();
        _oldestId = null;
        _foundOldest = false;
        _loading = false;
        HasError = false;
        IsLoading = false;
        Rebuild();

        if (View == FilesView.Recent)
            LoadRecent();
        else if (!ShowSources || SelectedSource is not null)
            _ = LoadAsync(reset: true);
    }

    private void LoadRecent()
    {
        var now = DateTime.Now;
        foreach (var record in _session.Files.Recent(RecentLimit))
        {
            ConversationKey key;
            try
            {
                key = ConversationKey.Parse(record.ConvKey);
            }
            catch (FormatException)
            {
                continue;
            }
            var link = ZulipHtml.UploadLinkOf(record.UploadPath, record.FileName);
            var senderId = _session.Messages.Get(record.MessageId)?.SenderId;
            AddEntry(new FileEntry(this, link, record.MessageId, key, record.LastOpenedAt ?? record.DownloadedAt,
                SourceText(senderId, key), record.Size, now, _session.Settings.Zulip.TwentyFourHourTime));
        }
        _foundOldest = true;
        Rebuild();
    }

    private async Task LoadAsync(bool reset)
    {
        var version = _version;
        _loading = true;
        IsLoading = true;

        var query = new MessageQuery(
            BuildNarrow(),
            reset ? "newest" : _oldestId!.Value.ToString(CultureInfo.InvariantCulture),
            PageSize,
            0,
            IncludeAnchor: reset);

        GetMessagesResult result;
        try
        {
            result = await _session.Client.GetMessagesAsync(query, _session.Token);
        }
        catch (Exception ex)
        {
            if (version != _version || _session.IsDisposed)
                return;
            Log.Warn("加载文件列表失败", ex);
            _loading = false;
            HasError = true;
            IsLoading = false;
            if (Groups.Count > 0)
                ShowToast("加载失败，请稍后再试");
            return;
        }
        if (version != _version || _session.IsDisposed)
            return;

        var now = DateTime.Now;
        foreach (var dto in Enumerable.Reverse(result.Messages))
        {
            var key = ConversationKey.FromMessage(dto, _session.SelfId);
            foreach (var link in ZulipHtml.ExtractUploads(dto.Content))
            {
                if (!_paths.Contains(link.Path))
                    AddEntry(new FileEntry(this, link, dto.Id, key, dto.Timestamp, SourceText(dto.SenderId, key), null, now, _session.Settings.Zulip.TwentyFourHourTime));
            }
        }
        if (result.Messages.Count > 0)
            _oldestId = result.Messages.Min(m => m.Id);
        _foundOldest = result.FoundOldest || result.Messages.Count == 0;
        _loading = false;
        IsLoading = false;
        Rebuild();
        ContinueIfHidden();
    }

    // 筛选后没有可见的文件但还有更早的消息：继续加载，否则会误显示“未找到”，列表也没有可滚动的内容触发加载
    private void ContinueIfHidden()
    {
        if (Groups.Count == 0)
            LoadOlder();
    }

    private void AddEntry(FileEntry entry)
    {
        if (!_paths.Add(entry.Path))
            return;
        entry.RefreshState(_session.Files);
        _entries.Add(entry);
        if (entry.Size is null)
        {
            _sizeQueue.Enqueue(entry);
            _ = PumpSizesAsync();
        }
    }

    // 文件大小逐个请求，避免一次发出上百个请求
    private async Task PumpSizesAsync()
    {
        if (_pumpingSizes)
            return;
        _pumpingSizes = true;
        try
        {
            while (_sizeQueue.Count > 0 && !_closed)
            {
                var entry = _sizeQueue.Dequeue();
                try
                {
                    entry.Size = await _session.Files.GetSizeAsync(entry.Path);
                }
                catch (Exception)
                {
                    // 退出账号时请求被取消；大小只是附加信息，失败就不显示
                    if (_session.IsDisposed)
                        return;
                }
            }
            if (!_closed && Sort == FileSort.Size)
                Rebuild();
        }
        finally
        {
            _pumpingSizes = false;
        }
    }

    private List<NarrowTerm> BuildNarrow()
    {
        List<NarrowTerm> narrow = [];
        switch (SelectedSource)
        {
            case { UserId: { } userId }:
                narrow.Add(Narrow.Sender(userId));
                break;
            case { Key: { } key }:
                narrow.AddRange(Narrow.For(key, _session.SelfId, key is TopicKey t ? _session.Conversations.GetTopicName(t) : null));
                break;
        }
        narrow.Add(Narrow.Has("attachment"));
        if (Query.Trim() is { Length: > 0 } q)
            narrow.Add(Narrow.Search(q));
        return narrow;
    }

    private string SourceText(long? senderId, ConversationKey key)
    {
        var title = _session.Conversations.GetTitle(key);
        if (senderId == _session.SelfId)
            return $"发给 {title}";
        if (senderId is not { } id)
            return title;
        var name = _session.Users.Get(id) is null ? "未知用户" : _session.Contacts.DisplayName(id);
        return $"来自 {name} | {title}";
    }

    // 按筛选、（最近使用的）搜索词和排序重建分组
    private void Rebuild()
    {
        IEnumerable<FileEntry> visible = _entries;
        if (Filter is { } kind)
            visible = visible.Where(e => e.Kind == kind);
        if (View == FilesView.Recent && Query.Trim() is { Length: > 0 } q)
            visible = visible.Where(e => e.FileName.Contains(q, StringComparison.OrdinalIgnoreCase));

        Groups.Clear();
        var sort = View == FilesView.Group ? Sort : FileSort.Time;
        if (sort == FileSort.Time)
        {
            FileGroup? group = null;
            foreach (var entry in visible)
            {
                if (group is null || group.Header != entry.GroupHeader)
                {
                    group = new FileGroup(entry.GroupHeader);
                    Groups.Add(group);
                }
                group.Entries.Add(entry);
            }
        }
        else
        {
            var sorted = sort == FileSort.Name
                ? visible.OrderBy(e => e.FileName, StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), true))
                : visible.OrderByDescending(e => e.Size ?? -1);
            var group = new FileGroup(null);
            foreach (var entry in sorted)
                group.Entries.Add(entry);
            if (group.Entries.Count > 0)
                Groups.Add(group);
        }
        FooterText = $"共 {Groups.Sum(g => g.Entries.Count)} 个文件";
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(IsLoadingMore));
    }
}
