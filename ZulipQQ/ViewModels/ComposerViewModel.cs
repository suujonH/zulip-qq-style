using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public enum AttachmentState
{
    Uploading,
    Done,
    Failed,
}

/// <summary>输入区中待发送的附件：上传中显示进度，图片完成后显示缩略图，随文字一起发送。</summary>
public partial class PendingAttachment(string localPath, bool isImage) : ViewModelBase
{
    public string LocalPath { get; } = localPath;

    public bool IsImage { get; } = isImage;

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>文件名：上传前为本地文件名，上传后以服务器返回的为准。</summary>
    [ObservableProperty]
    public partial string FileName { get; set; } = Path.GetFileName(localPath);

    [ObservableProperty]
    public partial Bitmap? Thumbnail { get; set; }

    /// <summary>上传进度 0～1。</summary>
    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUploading), nameof(IsFailed))]
    public partial AttachmentState State { get; set; }

    public bool IsUploading => State == AttachmentState.Uploading;

    public bool IsFailed => State == AttachmentState.Failed;

    /// <summary>服务器地址（<c>/user_uploads/…</c>），上传完成后才有。</summary>
    public string? Url { get; set; }
}

/// <summary>
/// 聊天的输入区：文字、待发送的图片、引用、@ 补全。文字变化 1 秒后保存草稿；
/// 文件（非图片）上传完成后立即单独发送，图片等用户点击发送后与文字一起发出（与 QQ 一致）。
/// </summary>
public partial class ComposerViewModel : ViewModelBase
{
    private static readonly TimeSpan DraftDelay = TimeSpan.FromSeconds(1);

    private readonly ChatViewModel _chat;
    private readonly ZulipSession _session;
    private readonly Action<string> _showToast;
    private readonly DispatcherTimer _draftTimer = new() { Interval = DraftDelay };

    // 载入草稿、发送后清空时不触发保存
    private bool _loadingText;
    private bool _draftDirty;
    private bool _opened;
    private bool _closed;

    // 最近一次草稿保存；关闭时等待它完成
    private Task _draftSave = Task.CompletedTask;

    public ComposerViewModel(ChatViewModel chat, ZulipSession session, Action<string> showToast)
    {
        _chat = chat;
        _session = session;
        _showToast = showToast;
        Mentions = new MentionCompletionViewModel(chat, session);
        _draftTimer.Tick += (_, _) => SaveDraft();
        Attachments.CollectionChanged += (_, _) =>
        {
            SendCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasAttachments));
        };
    }

    public EmojiService Emoji => _session.Emoji;

    /// <summary>所属的聊天。</summary>
    public ChatViewModel Chat => _chat;

    public bool HasAttachments => Attachments.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial string Text { get; set; } = "";

    public ObservableCollection<PendingAttachment> Attachments { get; } = [];

    /// <summary>引用的消息，显示在输入框上方。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQuote), nameof(QuoteText))]
    public partial MessageItemViewModel? QuoteTarget { get; set; }

    public bool HasQuote => QuoteTarget is not null;

    public string QuoteText => QuoteTarget is { } q ? $"{q.SenderName}：{ZulipHtml.ToPreview(q.ContentHtml)}" : "";

    /// <summary>话题所在频道是否允许自己发言；没有权限时输入框禁用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool HasSendPermission { get; private set; } = true;

    public string Placeholder => HasSendPermission ? "" : "你没有在此频道发言的权限";

    /// <summary>正在准备发送（获取引用原文）：此时禁止编辑，避免发送内容与输入框不一致。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsSending { get; private set; }

    /// <summary>输入框是否可以编辑。</summary>
    public bool CanEdit => HasSendPermission && !IsSending;

    /// <summary>Zulip 的 enter_sends：true 为 Enter 发送，false 为 Ctrl+Enter 发送。</summary>
    public bool EnterSends => _session.Settings.Zulip.EnterSends;

    public MentionCompletionViewModel Mentions { get; }

    /// <summary>请求把焦点移到输入框（设置引用之后）。</summary>
    public event EventHandler? FocusRequested;

    /// <summary>会话打开后载入草稿与发言权限。</summary>
    public void Open()
    {
        if (_opened)
            return;
        _opened = true;
        _loadingText = true;
        Text = _session.Drafts.Get(_chat.Key) ?? "";
        _loadingText = false;
        _draftDirty = false;
        RefreshPermission();
        _session.Settings.Changed += OnSettingChanged;
        _session.PermissionsChanged += OnPermissionsChanged;
        _session.Channels.ChannelChanged += OnChannelChanged;
    }

    /// <summary>
    /// 会话关闭：取消进行中的上传，立即保存草稿并等待完成
    /// （退出账号时随后会销毁会话，不等待的话草稿请求会被取消）。
    /// </summary>
    public Task CloseAsync()
    {
        if (_closed)
            return Task.CompletedTask;
        _closed = true;
        _draftTimer.Stop();
        foreach (var a in Attachments)
            a.Cancellation.Cancel();
        _session.Settings.Changed -= OnSettingChanged;
        _session.PermissionsChanged -= OnPermissionsChanged;
        _session.Channels.ChannelChanged -= OnChannelChanged;
        if (_session.IsDisposed)
            return Task.CompletedTask;
        if (_draftDirty)
        {
            _draftDirty = false;
            _draftSave = _session.Drafts.SetAsync(_chat.Key, Text);
        }
        return _draftSave;
    }

    /// <summary>
    /// 会话的键被替换（话题整体移动）：重新计算发言权限；已保存在旧键下的草稿改存到新键下。
    /// </summary>
    public void OnKeyChanged(ConversationKey oldKey)
    {
        RefreshPermission();
        if (!_opened || _closed || oldKey.Equals(_chat.Key) || _session.Drafts.Get(oldKey) is null)
            return;
        _session.Forget(_session.Drafts.DeleteAsync(oldKey), "删除草稿");
        _draftDirty = true;
        SaveDraft();
    }

    /// <summary>引用一条消息，并把焦点移到输入框。</summary>
    public void Quote(MessageItemViewModel message)
    {
        if (message.Id <= 0)
            return;
        QuoteTarget = message;
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void ClearQuote() => QuoteTarget = null;

    partial void OnTextChanged(string value)
    {
        if (_loadingText || _closed || !_opened)
            return;
        _draftDirty = true;
        _draftTimer.Stop();
        _draftTimer.Start();
    }

    private void SaveDraft()
    {
        _draftTimer.Stop();
        if (!_draftDirty || _closed)
            return;
        _draftDirty = false;
        _draftSave = _session.Drafts.SetAsync(_chat.Key, Text);
        _session.Forget(_draftSave, "保存草稿");
    }

    private bool CanSend() =>
        HasSendPermission && !IsSending
        && Attachments.All(a => a.State != AttachmentState.Uploading)
        && (!string.IsNullOrWhiteSpace(Text) || Attachments.Any(a => a.State == AttachmentState.Done));

    /// <summary>
    /// 发送：引用块 + 文字 + 已上传的图片链接合成一条消息。超过服务器长度限制时不发送；
    /// 发送前清空输入区并删除草稿。消息本身在后台发送（本地回显立即出现）。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var key = _chat.Key;
        var quote = QuoteTarget;
        // 等待引用原文期间可能又添加了图片：只发送、移除现在已有的附件
        var captured = Attachments.ToList();
        var images = captured.Where(a => a.State == AttachmentState.Done).ToList();
        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        try
        {
            var content = new StringBuilder();
            if (quote is not null)
            {
                string raw;
                try
                {
                    raw = await _session.Chat.GetRawContentAsync(quote.Id);
                }
                catch (Exception ex)
                {
                    if (!_session.IsDisposed)
                    {
                        Log.Warn($"获取引用原文失败：{quote.Id}", ex);
                        _showToast("引用失败");
                    }
                    return;
                }
                if (_closed)
                    return;
                content.Append(QuoteBlock(key, quote, raw));
            }
            content.Append(Text.TrimEnd());
            foreach (var image in images)
            {
                if (content.Length > 0 && content[^1] != '\n')
                    content.Append('\n');
                content.Append(LinkMarkdown(image.FileName, image.Url!));
            }

            var text = content.ToString();
            if (_session.Realm.MaxMessageLength > 0 && text.Length > _session.Realm.MaxMessageLength)
            {
                _showToast("消息过长");
                return;
            }

            _loadingText = true;
            Text = "";
            _loadingText = false;
            _draftDirty = false;
            _draftTimer.Stop();
            foreach (var a in captured)
                Attachments.Remove(a);
            QuoteTarget = null;
            Mentions.Close();
            // 关闭时等待删除完成，否则退出账号会取消请求，已发送的草稿下次登录又出现
            _draftSave = _session.Drafts.DeleteAsync(_chat.Key);
            _session.Forget(_draftSave, "删除草稿");
            _session.Forget(SendToLatestAsync(text), "发送消息");
        }
        finally
        {
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            if (quote is not null)
                FocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // 正在查看较早的消息时，本地回显和新消息不会追加到时间线：先回到最新的消息再发送。
    // 会话的键在 await 之后取：话题可能在上传或加载期间被移动
    private async Task SendToLatestAsync(string content)
    {
        await _chat.ShowLatestAsync();
        if (!_session.IsDisposed)
            await _session.Chat.SendAsync(_chat.Key, content);
    }

    [RelayCommand]
    private async Task SetEnterSendsAsync(bool value)
    {
        if (EnterSends == value)
            return;
        try
        {
            await _session.Settings.SetZulipAsync("enter_sends", value);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("修改发送快捷键失败", ex);
            _showToast("设置失败");
        }
    }

    /// <summary>选择一个文件，上传完成后立即单独发送。</summary>
    public async Task PickFileAsync(TopLevel top)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "发送文件", AllowMultiple = false });
        if (!_closed && files.Count > 0 && files[0].TryGetLocalPath() is { } path)
            AddFiles([path]);
    }

    /// <summary>选择图片（可多选），上传后显示在输入区，等点击发送时与文字一起发出。</summary>
    public async Task PickImagesAsync(TopLevel top)
    {
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "发送图片",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("图片") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.gif", "*.webp", "*.bmp"] }],
        });
        if (!_closed)
            AddFiles(files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList());
    }

    /// <summary>添加本地文件（选择、拖放、粘贴）：图片进入待发送列表，其它文件上传后立即发送。</summary>
    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (!HasSendPermission)
            return;
        foreach (var path in paths)
        {
            if (!File.Exists(path) || !CheckSize(path))
                continue;
            if (ZulipHtml.IsImagePath(path))
                AddImage(path);
            else
                _ = UploadAndSendFileAsync(path);
        }
    }

    /// <summary>粘贴的位图：存为临时 png 后按图片处理。</summary>
    public void AddBitmap(Bitmap bitmap)
    {
        if (!HasSendPermission)
            return;
        try
        {
            var dir = Path.Combine(AppPaths.CacheDir, "paste");
            Directory.CreateDirectory(dir);
            var path = FileNameDeduper.NextFreePath(dir, $"截图{DateTime.Now:yyyyMMddHHmmss}.png");
            bitmap.Save(path, PngBitmapEncoderOptions.Default);
            AddImage(path);
        }
        catch (Exception ex)
        {
            Log.Warn("保存粘贴的图片失败", ex);
            _showToast("图片粘贴失败");
        }
    }

    [RelayCommand]
    private void RemoveAttachment(PendingAttachment attachment)
    {
        attachment.Cancellation.Cancel();
        Attachments.Remove(attachment);
    }

    private bool CheckSize(string path)
    {
        var max = _session.Realm.MaxFileUploadSizeMib;
        if (max <= 0 || new FileInfo(path).Length <= max * 1024L * 1024L)
            return true;
        _showToast($"文件超过 {max}MB，无法发送");
        return false;
    }

    private void AddImage(string path)
    {
        var attachment = new PendingAttachment(path, isImage: true) { Thumbnail = LoadThumbnail(path) };
        Attachments.Add(attachment);
        _ = UploadAsync(attachment);
    }

    private async Task UploadAndSendFileAsync(string path)
    {
        var attachment = new PendingAttachment(path, isImage: false);
        Attachments.Add(attachment);
        await UploadAsync(attachment);
        if (_closed || attachment.State != AttachmentState.Done || !Attachments.Remove(attachment))
            return;
        _session.Forget(SendToLatestAsync(LinkMarkdown(attachment.FileName, attachment.Url!)), "发送文件");
    }

    // 上传失败时保留在列表中（显示失败），由用户移除
    private async Task UploadAsync(PendingAttachment attachment)
    {
        try
        {
            var result = await _session.Chat.UploadAsync(attachment.LocalPath,
                new Progress<double>(p => attachment.Progress = p), attachment.Cancellation.Token);
            if (_closed || _session.IsDisposed)
                return;
            attachment.Url = result.Url;
            if (!string.IsNullOrEmpty(result.Filename))
                attachment.FileName = result.Filename;
            attachment.State = AttachmentState.Done;
        }
        catch (OperationCanceledException) when (attachment.Cancellation.IsCancellationRequested || _session.IsDisposed)
        {
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"上传失败：{attachment.LocalPath}", ex);
            attachment.State = AttachmentState.Failed;
            _showToast("上传失败");
        }
        finally
        {
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    private static Bitmap? LoadThumbnail(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, 120);
        }
        catch (Exception ex)
        {
            Log.Debug($"生成缩略图失败：{path}", ex);
            return null;
        }
    }

    /// <summary><c>[文件名](地址)</c>；文件名中的方括号转义。</summary>
    internal static string LinkMarkdown(string fileName, string url) =>
        $"[{fileName.Replace("[", "\\[", StringComparison.Ordinal).Replace("]", "\\]", StringComparison.Ordinal)}]({url})";

    /// <summary>
    /// 与 Zulip 网页端相同的引用格式：<c>@_**名字|id** [说](消息链接):</c> 加 quote 代码块。
    /// 原文中含有 ``` 时加长围栏，避免提前结束。
    /// </summary>
    private string QuoteBlock(ConversationKey key, MessageItemViewModel quote, string raw)
    {
        var sender = _session.Users.Get(quote.Message.SenderId)?.FullName ?? quote.Message.SenderName;
        var channel = key is TopicKey t ? _session.Channels.Get(t.StreamId)?.Name ?? "" : "";
        var topic = key is TopicKey tk ? _session.Conversations.GetTopicName(tk) : "";
        var link = ZulipLinks.MessageLink(_session.Client.RealmUrl, key, _session.SelfId, quote.Id, channel, topic);
        var fence = "```";
        while (raw.Contains(fence, StringComparison.Ordinal))
            fence += "`";
        return $"@_**{sender}|{quote.Message.SenderId}** [说]({link}):\n{fence}quote\n{raw}\n{fence}\n";
    }

    private void RefreshPermission() =>
        HasSendPermission = _chat.Key is not TopicKey t || _session.Channels.Get(t.StreamId) is not { } channel
                            || _session.Permissions.CanSendToChannel(channel);

    private void OnSettingChanged(object? sender, string name)
    {
        if (name == "enter_sends")
            OnPropertyChanged(nameof(EnterSends));
    }

    private void OnPermissionsChanged(object? sender, EventArgs e) => RefreshPermission();

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (_chat.Key is TopicKey t && t.StreamId == streamId)
            RefreshPermission();
    }
}
