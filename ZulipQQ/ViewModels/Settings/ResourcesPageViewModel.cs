using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>引用上传文件的一条消息：展开文件后列出。</summary>
public sealed partial class ResourceReference(ResourcesPageViewModel owner, ConversationKey key, long messageId,
    string groupName, string timeText, string preview) : ViewModelBase
{
    public ConversationKey Key { get; } = key;

    public long MessageId { get; } = messageId;

    /// <summary>会话标题（与会话列表相同，有备注时只显示备注）。</summary>
    public string GroupName { get; } = groupName;

    public string TimeText { get; } = timeText;

    /// <summary>单行内容预览，显示不下时由视图截断。</summary>
    public string Preview { get; } = preview;

    [RelayCommand]
    private Task JumpAsync() => owner.JumpAsync(Key, MessageId);
}

/// <summary>“资源”页的一个文件。默认收起，点击后展开引用它的消息（第一次展开时载入）。</summary>
public sealed partial class ResourceItem : ViewModelBase
{
    private readonly ResourcesPageViewModel _owner;
    private bool _referencesLoaded;

    public ResourceItem(ResourcesPageViewModel owner, AttachmentDto dto, DateTime now, bool use24h)
    {
        _owner = owner;
        Id = dto.Id;
        CreateTime = dto.CreateTimeSeconds;
        Size = dto.Size;
        var path = "/user_uploads/" + dto.PathId;
        Link = new UploadLink(path, dto.Name, FileKinds.Of(path) == FileKind.Image);
        MessageIds = dto.AllMessageIds;
        // 被多条消息引用时打开用最早的一条（消息 id 随发送递增）
        FirstMessageId = MessageIds.Count > 0 ? MessageIds.Min() : null;
        var time = TimeText.FromUnix(CreateTime);
        var usage = MessageIds.Count > 0 ? $"{MessageIds.Count} 条消息中引用" : "未被引用";
        DetailText = $"{FileCard.FormatSize(dto.Size)} · {TimeText.ListTime(time, now, use24h)} 上传 · {usage}";
    }

    public long Id { get; }

    /// <summary>上传时间，Unix 秒。</summary>
    public long CreateTime { get; }

    public long Size { get; }

    public UploadLink Link { get; }

    public string FileName => Link.FileName;

    public bool IsImage => Link.IsImage;

    /// <summary>缩略图地址；非图片为 null，避免隐藏的图片控件把整个文件下载下来。</summary>
    public string? ImagePath => IsImage ? Link.Path : null;

    public string IconKey => FileKinds.IconKey(Link.Path);

    public IReadOnlyList<long> MessageIds { get; }

    /// <summary>引用这个文件的最早一条消息；没有被引用时为 null。</summary>
    public long? FirstMessageId { get; }

    /// <summary>没有被引用的文件不知道属于哪个会话，不能打开（按会话下载），也没有可展开的内容。</summary>
    public bool HasMessages => FirstMessageId is not null;

    /// <summary>“大小 · 上传时间 · 引用情况”。</summary>
    public string DetailText { get; }

    [ObservableProperty]
    public partial bool IsExpanded { get; private set; }

    public ObservableCollection<ResourceReference> References { get; } = [];

    /// <summary>展开后列表上方的提示（加载中、加载失败、消息都看不到了）；有消息时为 null。</summary>
    [ObservableProperty]
    public partial string? ReferencesStatus { get; set; }

    [RelayCommand]
    private void Toggle()
    {
        if (!HasMessages)
            return;
        IsExpanded = !IsExpanded;
        if (IsExpanded && !_referencesLoaded)
        {
            _referencesLoaded = true;
            _ = _owner.LoadReferencesAsync(this);
        }
    }

    /// <summary>载入失败后允许下次展开时重试。</summary>
    public void ResetReferences() => _referencesLoaded = false;

    [RelayCommand]
    private Task OpenAsync() => _owner.OpenAsync(this);

    [RelayCommand]
    private Task SaveAsync() => _owner.SaveAsync(this);

    [RelayCommand]
    private Task DeleteAsync() => _owner.DeleteAsync(this);
}

/// <summary>资源：自己上传到 Zulip 的文件与已使用的存储空间。打开页面时载入，收到 attachment 事件或重新同步后重新载入。</summary>
public sealed partial class ResourcesPageViewModel : SettingsPageViewModel
{
    private readonly Dictionary<long, ConversationKey> _messageKeys = [];
    private List<ResourceItem> _all = [];
    private int _loadVersion;
    private bool _closed;

    public ResourcesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Session.AttachmentsChanged += OnChanged;
        Session.Resynced += OnChanged;
        _ = LoadAsync();
    }

    /// <summary>按搜索词过滤后的文件，最近上传的在前。</summary>
    public ObservableCollection<ResourceItem> Items { get; } = [];

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    [ObservableProperty]
    public partial bool IsLoading { get; private set; } = true;

    [ObservableProperty]
    public partial bool LoadFailed { get; private set; }

    /// <summary>“已使用 X / 配额”；没有配额时只有已使用；未载入时为 null。</summary>
    [ObservableProperty]
    public partial string? UsageText { get; private set; }

    /// <summary>已使用占配额的百分比；没有配额时为 null（不显示进度条）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuota))]
    public partial double? UsagePercent { get; private set; }

    public bool HasQuota => UsagePercent is not null;

    /// <summary>列表为空时的提示；有文件时为 null。</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; } = "加载中…";

    partial void OnQueryChanged(string value) => Rebuild();

    [RelayCommand]
    private Task RetryAsync() => LoadAsync();

    public override void Close()
    {
        _closed = true;
        Session.AttachmentsChanged -= OnChanged;
        Session.Resynced -= OnChanged;
    }

    /// <summary>以原文件名保存到“接收或下载的文件保存到”的目录。</summary>
    public async Task SaveAsync(ResourceItem item)
    {
        var saved = await Session.Files.SaveAsync(item.Link);
        if (!Session.IsDisposed)
            Owner.ShowToast(saved is not null ? FileService.SavedToast(saved) : "文件保存失败");
    }

    public async Task OpenAsync(ResourceItem item)
    {
        if (item.FirstMessageId is not { } messageId || await KeyOfAsync(messageId) is not { } key)
            return;
        if (!await Session.Files.OpenAsync(item.Link, key, messageId) && !Session.IsDisposed)
            Owner.ShowToast("文件下载失败");
    }

    /// <summary>在主面板（或会话的独立窗口）中打开引用文件的消息。</summary>
    public async Task JumpAsync(ConversationKey key, long messageId)
    {
        if (Owner.Main is not { } main)
            return;
        await main.OpenChatAsync(key, messageId);
        // 会话在独立窗口中打开时由该窗口显示在前台，不再激活主面板
        if (!Session.IsDisposed && main.ShowsInRightPane(key))
            App.Current.ShowMainWindow(main);
    }

    /// <summary>
    /// 展开文件时载入引用它的消息，按发送时间排列。逐条取消息，取不到的（已删除、没有权限）跳过；
    /// 全部取不到时显示提示，下次展开时重试。
    /// </summary>
    public async Task LoadReferencesAsync(ResourceItem item)
    {
        item.ReferencesStatus = "加载中…";
        var results = await Task.WhenAll(item.MessageIds.Select(async id =>
        {
            try
            {
                return await Session.Client.GetMessageAsync(id, applyMarkdown: true, Session.Token);
            }
            catch (Exception ex)
            {
                // 退出账号后请求被取消，不记录
                if (!Session.IsDisposed)
                    Log.Warn($"取得消息失败：{id}", ex);
                return null;
            }
        }));
        if (_closed || Session.IsDisposed)
            return;
        var now = DateTime.Now;
        var use24h = Session.Settings.Zulip.TwentyFourHourTime;
        item.References.Clear();
        foreach (var dto in results.OfType<MessageDto>().OrderBy(m => m.Timestamp).ThenBy(m => m.Id))
        {
            var key = ConversationKey.FromMessage(dto, Session.SelfId);
            _messageKeys[dto.Id] = key;
            item.References.Add(new ResourceReference(this, key, dto.Id,
                Session.Conversations.GetDisplayTitle(key),
                TimeText.ListTime(TimeText.FromUnix(dto.Timestamp), now, use24h), ZulipHtml.ToPreview(dto.Content)));
        }
        item.ReferencesStatus = item.References.Count > 0 ? null : "引用它的消息已删除或无法查看";
        if (item.References.Count == 0)
            item.ResetReferences();
    }

    public async Task DeleteAsync(ResourceItem item)
    {
        if (!await Owner.ConfirmAsync($"删除“{item.FileName}”？引用它的消息中的链接将失效，删除后不能恢复。", "删除", isDanger: true))
            return;
        try
        {
            await Session.Client.DeleteAttachmentAsync(item.Id, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"删除上传的文件失败：{item.Id}", ex);
            Owner.ShowToast("删除失败");
            return;
        }
        // attachment 事件随后会重新载入；先移除，避免删除后仍显示到事件到达
        _all.Remove(item);
        Rebuild();
    }

    private void OnChanged(object? sender, EventArgs e) => _ = LoadAsync();

    // 连续触发时只采用最后一次载入的结果
    private async Task LoadAsync()
    {
        var version = ++_loadVersion;
        IsLoading = true;
        LoadFailed = false;
        if (_all.Count == 0)
            StatusText = "加载中…";
        try
        {
            var (attachments, used) = await Session.Client.GetAttachmentsAsync(Session.Token);
            if (_closed || version != _loadVersion)
                return;
            var now = DateTime.Now;
            var use24h = Session.Settings.Zulip.TwentyFourHourTime;
            _all = attachments.Select(a => new ResourceItem(this, a, now, use24h))
                .OrderByDescending(i => i.CreateTime).ThenByDescending(i => i.Id).ToList();
            SetUsage(used);
        }
        catch (Exception ex)
        {
            // 退出账号后请求被取消，静默结束
            if (_closed || version != _loadVersion || Session.IsDisposed)
                return;
            Log.Warn("载入上传的文件失败", ex);
            LoadFailed = true;
            if (_all.Count > 0)
                Owner.ShowToast("刷新失败");
        }
        finally
        {
            if (version == _loadVersion)
                IsLoading = false;
        }
        Rebuild();
    }

    private void SetUsage(long used)
    {
        if (Session.Realm.UploadQuotaMib is { } quota)
        {
            var bytes = quota * 1024 * 1024;
            UsageText = $"已使用 {FileCard.FormatSize(used)} / {FileCard.FormatSize(bytes)}";
            UsagePercent = bytes > 0 ? Math.Min(100, used * 100.0 / bytes) : 100;
        }
        else
        {
            UsageText = $"已使用 {FileCard.FormatSize(used)}";
            UsagePercent = null;
        }
    }

    private void Rebuild()
    {
        var query = Query.Trim();
        var shown = query.Length == 0
            ? _all
            : _all.Where(i => i.FileName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        Items.Clear();
        foreach (var item in shown)
            Items.Add(item);
        StatusText = Items.Count > 0 ? null
            : LoadFailed && _all.Count == 0 ? "加载失败"
            : IsLoading && _all.Count == 0 ? "加载中…"
            : _all.Count == 0 ? "还没有上传过文件"
            : "没有找到相关文件";
    }

    /// <summary>消息所属的会话：向服务器取一次消息，之后缓存。取不到时提示并返回 null。</summary>
    private async Task<ConversationKey?> KeyOfAsync(long messageId)
    {
        if (_messageKeys.TryGetValue(messageId, out var key))
            return key;
        try
        {
            var dto = await Session.Client.GetMessageAsync(messageId, applyMarkdown: false, Session.Token);
            key = ConversationKey.FromMessage(dto, Session.SelfId);
            _messageKeys[messageId] = key;
            return key;
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn($"取得消息失败：{messageId}", ex);
            Owner.ShowToast("找不到引用这个文件的消息");
            return null;
        }
    }
}
