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
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

public enum FavoriteCategory
{
    All,
    Chat,
    Media,
    Files,
    Links,
}

/// <summary>
/// 收藏中的一条消息。预览：只有附件的消息显示缩略图或文件图标与文件名，其它显示最多 3 行纯文本
/// （有图片时同时显示第一张的缩略图）。
/// </summary>
public sealed partial class FavoriteItem : ViewModelBase
{
    private readonly FavoritesViewModel _owner;

    public FavoriteItem(FavoritesViewModel owner, MessageDto dto, ConversationKey key, string senderName)
    {
        _owner = owner;
        MessageId = dto.Id;
        Key = key;
        SenderName = senderName;
        Content = dto.Content;
        Timestamp = dto.Timestamp;
        DateText = TimeText.FromUnix(dto.Timestamp).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        var images = ZulipHtml.ExtractInlineImages(dto.Content);
        var uploads = ZulipHtml.ExtractUploads(dto.Content);
        Thumbnail = images.Count > 0 ? images[0]
            : uploads.FirstOrDefault(u => u.IsImage) is { } image ? new InlineImage(image.Path, image.Path)
            : null;
        if (ZulipHtml.IsAttachmentOnly(dto.Content))
        {
            if (Thumbnail is null)
                File = uploads.FirstOrDefault();
        }
        else
        {
            Text = ZulipHtml.ToPlainText(dto.Content);
        }
    }

    public long MessageId { get; }

    public ConversationKey Key { get; }

    public long Timestamp { get; }

    /// <summary>消息原始 HTML（复制、转发预览用）。</summary>
    public string Content { get; }

    public string SenderName { get; }

    public string FromText => $"来自：{SenderName}";

    public string DateText { get; }

    public string? Text { get; }

    public InlineImage? Thumbnail { get; }

    public UploadLink? File { get; }

    public string? FileIconKey => File is { } f ? FileKinds.IconKey(f.Path) : null;

    [RelayCommand]
    private void Locate() => _owner.Locate(this);

    [RelayCommand]
    private Task UnstarAsync() => _owner.UnstarAsync(this);

    [RelayCommand]
    private void Copy() => _owner.Copy(this);

    [RelayCommand]
    private void Forward() => _owner.Forward(this);
}

/// <summary>
/// 收藏窗口：已加星标的消息，按分类和搜索词筛选，最新的在上面，每页 50 条，滚动到底部附近时继续加载。
/// 分类“图片与视频”“文件”按 <c>has:attachment</c> 查询后在客户端按类型取舍（<c>has:image</c> 不含视频），
/// 与聊天记录窗口一致，因此只有外部图片的消息不出现在“图片与视频”中。
/// 星标被取消时移除对应条目；新增星标时（没有搜索词）重新加载。
/// </summary>
public partial class FavoritesViewModel : ViewModelBase
{
    private const int PageSize = 50;
    private static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(400);

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly DispatcherTimer _searchTimer = new() { Interval = SearchDelay };

    private int _version;
    private long? _oldestId;
    private bool _foundOldest;
    private bool _loading;
    private bool _closed;
    private int _toastVersion;

    public FavoritesViewModel(ZulipSession session, MainViewModel main)
    {
        _session = session;
        _main = main;
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            Reload();
        };
        session.Messages.StarredChanged += OnStarredChanged;
        session.Resynced += OnResynced;
    }

    public ZulipSession Session => _session;

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAll), nameof(IsChat), nameof(IsMedia), nameof(IsFiles), nameof(IsLinks), nameof(CategoryTitle))]
    public partial FavoriteCategory Category { get; private set; }

    public bool IsAll => Category == FavoriteCategory.All;

    public bool IsChat => Category == FavoriteCategory.Chat;

    public bool IsMedia => Category == FavoriteCategory.Media;

    public bool IsFiles => Category == FavoriteCategory.Files;

    public bool IsLinks => Category == FavoriteCategory.Links;

    public string CategoryTitle => Category switch
    {
        FavoriteCategory.Chat => "聊天记录",
        FavoriteCategory.Media => "图片与视频",
        FavoriteCategory.Files => "文件",
        FavoriteCategory.Links => "链接",
        _ => "全部收藏",
    };

    public ObservableCollection<FavoriteItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsLoadingMore))]
    public partial bool IsLoading { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    public partial bool HasError { get; private set; }

    /// <summary>列表中间的状态文字；有内容时为 null。</summary>
    public string? StatusText =>
        Items.Count > 0 ? null
        : HasError ? "加载失败"
        : IsLoading ? "加载中…"
        : Query.Trim().Length > 0 ? "未找到相关收藏"
        : "暂无收藏";

    public bool IsLoadingMore => IsLoading && Items.Count > 0;

    [ObservableProperty]
    public partial string? ToastText { get; private set; }

    /// <summary>需要把主窗口带到前台（定位、转发之后）。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>需要复制文字到剪贴板（由窗口执行）。</summary>
    public event EventHandler<string>? CopyRequested;

    public void Start()
    {
        _searchTimer.Stop();
        Reload();
    }

    public void Close()
    {
        _closed = true;
        _searchTimer.Stop();
        _version++;
        _session.Messages.StarredChanged -= OnStarredChanged;
        _session.Resynced -= OnResynced;
    }

    partial void OnQueryChanged(string value)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    [RelayCommand]
    private void SelectCategory(FavoriteCategory category)
    {
        if (Category == category)
            return;
        Category = category;
        Reload();
    }

    /// <summary>滚动到底部附近时加载更早的收藏；上一页失败时再次滚动即重试。</summary>
    public void LoadOlder()
    {
        if (!_loading && !_foundOldest && _oldestId is not null)
            _ = LoadAsync(reset: false);
    }

    /// <summary>在主窗口中打开会话并定位到这条消息（双击、“定位到聊天”）。</summary>
    public void Locate(FavoriteItem item) => _ = LocateAsync(item);

    private async Task LocateAsync(FavoriteItem item)
    {
        await _main.OpenChatAsync(item.Key, item.MessageId);
        if (!_closed)
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public async Task UnstarAsync(FavoriteItem item)
    {
        try
        {
            await _session.Chat.SetStarredAsync([item.MessageId], false);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("取消收藏失败", ex);
            ShowToast("操作失败");
            return;
        }
        // 事件通常随后到达；先移除，事件到达时不会重复处理
        if (!_closed)
            Remove([item.MessageId]);
    }

    public void Copy(FavoriteItem item) => CopyRequested?.Invoke(this, ZulipHtml.ToPlainText(item.Content));

    /// <summary>转发：对话框显示在主窗口中，先把主窗口带到前台。</summary>
    public void Forward(FavoriteItem item)
    {
        var session = _session;
        var id = item.MessageId;
        MainWindowRequested?.Invoke(this, EventArgs.Empty);
        ForwardDialogViewModel.Show(_main, $"{item.SenderName}：{ZulipHtml.ToPreview(item.Content)}",
            async () => [await session.Chat.GetRawContentAsync(id)]);
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

    private void OnStarredChanged(object? sender, (IReadOnlyList<long> Ids, bool Added) e)
    {
        if (_closed)
            return;
        if (!e.Added)
            Remove(e.Ids);
        else if (Query.Trim().Length == 0)
            Reload();
    }

    private void OnResynced(object? sender, EventArgs e) => Reload();

    private void Remove(IReadOnlyCollection<long> ids)
    {
        foreach (var item in Items.Where(i => ids.Contains(i.MessageId)).ToList())
            Items.Remove(item);
        OnPropertyChanged(nameof(StatusText));
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
            Items.Clear();
            _oldestId = null;
            _foundOldest = false;
            HasError = false;
        }
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
            Log.Warn("加载收藏失败", ex);
            _loading = false;
            HasError = true;
            IsLoading = false;
            if (Items.Count > 0)
                ShowToast("加载失败，请稍后再试");
            return;
        }
        if (version != _version || _session.IsDisposed)
            return;

        var appended = false;
        foreach (var dto in Enumerable.Reverse(result.Messages))
        {
            if (!Matches(dto) || Items.Any(i => i.MessageId == dto.Id))
                continue;
            Items.Add(CreateItem(dto));
            appended = true;
        }
        if (result.Messages.Count > 0)
            _oldestId = result.Messages.Min(m => m.Id);
        _foundOldest = result.FoundOldest || result.Messages.Count == 0;
        _loading = false;
        // 这一页都被客户端筛掉了：继续向前加载，否则会误显示“暂无收藏”
        if (!appended && !_foundOldest)
        {
            await LoadAsync(reset: false);
            return;
        }
        IsLoading = false;
        OnPropertyChanged(nameof(StatusText));
    }

    private List<NarrowTerm> BuildNarrow()
    {
        List<NarrowTerm> narrow = [Narrow.Is("starred")];
        switch (Category)
        {
            case FavoriteCategory.Chat:
                narrow.Add(Narrow.Has("image", negated: true));
                narrow.Add(Narrow.Has("attachment", negated: true));
                narrow.Add(Narrow.Has("link", negated: true));
                break;
            case FavoriteCategory.Media:
            case FavoriteCategory.Files:
                narrow.Add(Narrow.Has("attachment"));
                break;
            case FavoriteCategory.Links:
                narrow.Add(Narrow.Has("link"));
                narrow.Add(Narrow.Has("attachment", negated: true));
                break;
        }
        if (Query.Trim() is { Length: > 0 } q)
            narrow.Add(Narrow.Search(q));
        return narrow;
    }

    // 附件分类在客户端按类型取舍
    private bool Matches(MessageDto dto)
    {
        switch (Category)
        {
            case FavoriteCategory.Media:
                return ZulipHtml.ExtractInlineImages(dto.Content).Count > 0
                       || ZulipHtml.ExtractUploads(dto.Content).Any(u => u.IsImage || ZulipHtml.IsVideoPath(u.Path));
            case FavoriteCategory.Files:
                return ZulipHtml.ExtractUploads(dto.Content).Any(u => !u.IsImage && !ZulipHtml.IsVideoPath(u.Path));
            default:
                return true;
        }
    }

    private FavoriteItem CreateItem(MessageDto dto)
    {
        var name = _session.Users.Get(dto.SenderId) is null ? dto.SenderFullName : _session.Contacts.DisplayName(dto.SenderId);
        return new FavoriteItem(this, dto, ConversationKey.FromMessage(dto, _session.SelfId), name);
    }
}
