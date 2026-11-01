using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Plugins;
using ZulipQQ.Services;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels;

public enum AttachmentState
{
    Uploading,
    Done,
    Failed,
}

/// <summary>输入区中上传中的文件（不是图片）：显示进度，失败时显示失败，上传完成后单独发送。</summary>
public partial class PendingAttachment(string localPath) : ViewModelBase
{
    public string LocalPath { get; } = localPath;

    public CancellationTokenSource Cancellation { get; } = new();

    /// <summary>文件名：上传前为本地文件名，上传后以服务器返回的为准。</summary>
    [ObservableProperty]
    public partial string FileName { get; set; } = Path.GetFileName(localPath);

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
/// 聊天的输入区：文字、引用、@ 补全，以及编辑已发送的消息。文字变化 1 秒后保存草稿；
/// 图片立即上传并以链接插入正文（上传中显示占位），文件（非图片）上传完成后立即单独发送。
/// </summary>
public partial class ComposerViewModel : ViewModelBase
{
    private static readonly TimeSpan DraftDelay = TimeSpan.FromSeconds(1);

    /// <summary>常用语标题的长度上限（Zulip 服务器的限制）。</summary>
    private const int MaxSnippetTitleLength = 60;

    private readonly ChatViewModel _chat;
    private readonly ZulipSession _session;
    private readonly Action<string> _showToast;
    private readonly DispatcherTimer _draftTimer = new() { Interval = DraftDelay };
    private readonly TypingNotifier _typing;

    // 载入草稿、发送后清空时不触发保存
    private bool _loadingText;
    private bool _draftDirty;
    private bool _opened;
    private bool _closed;

    // 实际是否有发言权限；HasSendPermission 在开发者模式下总为 true
    private bool _isSendAllowed = true;

    // 最近一次草稿保存；关闭时等待它完成
    private Task _draftSave = Task.CompletedTask;

    // 上传中的图片：正文中是它的占位，上传完成后换成图片链接
    private readonly List<ImageUpload> _imageUploads = [];

    // 程序替换文字（上传完成替换占位）：照常保存草稿，但不发送“正在输入”
    private bool _programmaticEdit;

    // 正在获取原文的引用：正文中是引用占位，获取完成后换成引用块
    private int _pendingQuotes;

    // 编辑消息前输入框中的内容，结束编辑后恢复；消息编辑前的原文
    private string _stashText = "";
    private string _editOriginal = "";

    public ComposerViewModel(ChatViewModel chat, ZulipSession session, Action<string> showToast)
    {
        _chat = chat;
        _session = session;
        _showToast = showToast;
        Mentions = new MentionCompletionViewModel(chat, session);
        _typing = new TypingNotifier(session);
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

    /// <summary>话题所在频道是否允许自己发言；没有权限时输入框禁用（开发者模式下可以输入，发送时只模拟）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Placeholder), nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool HasSendPermission { get; private set; } = true;

    public string Placeholder => HasSendPermission ? "" : "你没有在此频道发言的权限";

    /// <summary>正在准备发送（插件过滤）：此时禁止编辑，避免发送内容与输入框不一致。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsSending { get; private set; }

    /// <summary>输入框是否可以编辑。</summary>
    public bool CanEdit => HasSendPermission && !IsSending;

    /// <summary>正在输入框中编辑的消息；为 null 时是普通输入。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditingMessage), nameof(EditPreviewText), nameof(SendButtonText))]
    [NotifyCanExecuteChangedFor(nameof(ScheduleCommand))]
    public partial MessageItemViewModel? EditTarget { get; private set; }

    public bool IsEditingMessage => EditTarget is not null;

    public string EditPreviewText => EditTarget is { } m ? ZulipHtml.ToPreview(m.ContentHtml) : "";

    public string SendButtonText => IsEditingMessage ? "保存" : "发送";

    /// <summary>输入框显示 Markdown 源码（否则显示渲染效果）。保存在本机设置中，所有输入框共用。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SourceModeToolTip))]
    public partial bool IsSourceMode { get; private set; }

    public string SourceModeToolTip => IsSourceMode ? "显示效果" : "显示源码";

    /// <summary>Zulip 的 enter_sends：true 为 Enter 发送，false 为 Ctrl+Enter 发送。</summary>
    public bool EnterSends => _session.Settings.Zulip.EnterSends;

    public MentionCompletionViewModel Mentions { get; }

    /// <summary>代码块语言补全（在 ``` 后输入时）。</summary>
    public CodeLanguageCompletionViewModel Languages { get; } = new();

    /// <summary>请求把焦点移到输入框。</summary>
    public event EventHandler? FocusRequested;

    /// <summary>请求在输入框的光标处插入 Markdown（前后按需加空格），并把焦点移到输入框。</summary>
    public event EventHandler<string>? InsertRequested;

    /// <summary>请求在输入框的光标处把 Markdown 作为独立的段落插入（前后补足空行），并把焦点移到输入框。</summary>
    public event EventHandler<string>? InsertBlockRequested;

    /// <summary>文字被整体替换（载入草稿、发送后清空、开始 / 结束编辑消息）：视图清空撤销记录。</summary>
    public event EventHandler? TextReset;

    /// <summary>会话打开后载入草稿与发言权限。</summary>
    public void Open()
    {
        if (_opened)
            return;
        _opened = true;
        _loadingText = true;
        // 去掉上次关闭时没有完成的图片上传与引用留下的占位
        Text = StaleUploadPattern().Replace(_session.Drafts.Get(_chat.Key) ?? "", "").Replace(QuotingPlaceholder, "", StringComparison.Ordinal);
        _loadingText = false;
        _draftDirty = false;
        IsSourceMode = _session.Settings.Local.ComposerSourceMode;
        TextReset?.Invoke(this, EventArgs.Empty);
        RefreshPermission();
        _session.Settings.Changed += OnSettingChanged;
        _session.Settings.Local.Changed += OnLocalSettingChanged;
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
        // 编辑中关闭：放弃编辑，恢复原来的输入内容后按原逻辑保存草稿
        EndEdit(focus: false);
        _closed = true;
        _draftTimer.Stop();
        _typing.Stop();
        foreach (var a in Attachments)
            a.Cancellation.Cancel();
        foreach (var u in _imageUploads)
            u.Cancellation.Cancel();
        _session.Settings.Changed -= OnSettingChanged;
        _session.Settings.Local.Changed -= OnLocalSettingChanged;
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
        _typing.Stop();
        RefreshPermission();
        if (!_opened || _closed || oldKey.Equals(_chat.Key) || _session.Drafts.Get(oldKey) is null)
            return;
        _session.Forget(_session.Drafts.DeleteAsync(oldKey), "删除草稿");
        _draftDirty = true;
        SaveDraft();
    }

    /// <summary>
    /// 引用一条消息（与 Zulip 网页端的“引用消息”相同）：在光标处作为独立的段落插入占位 [引用…]，并把焦点移到输入框；
    /// 获取到消息原文后把占位换成引用块（见 <see cref="QuoteMarkdown"/>）。多次引用即可引用多条消息。
    /// </summary>
    public void Quote(MessageItemViewModel message)
    {
        if (message.Id <= 0 || !CanEdit)
            return;
        _pendingQuotes++;
        SendCommand.NotifyCanExecuteChanged();
        ScheduleCommand.NotifyCanExecuteChanged();
        InsertBlockRequested?.Invoke(this, QuotingPlaceholder);
        _ = QuoteAsync(message);
    }

    // 获取失败时去掉占位；占位相同，与网页版一样替换第一个
    private async Task QuoteAsync(MessageItemViewModel message)
    {
        try
        {
            var raw = await _session.Chat.GetRawContentAsync(message.Id);
            if (_closed || _session.IsDisposed)
                return;
            ReplacePlaceholder(QuotingPlaceholder, QuoteBlock(message, raw));
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"获取引用原文失败：{message.Id}", ex);
            if (_closed)
                return;
            ReplacePlaceholder(QuotingPlaceholder, "");
            _showToast("引用失败");
        }
        finally
        {
            _pendingQuotes--;
            SendCommand.NotifyCanExecuteChanged();
            ScheduleCommand.NotifyCanExecuteChanged();
        }
    }

    /// <summary>在输入框的光标处 @ 某人（与 @ 补全插入的格式相同），并把焦点移到输入框。</summary>
    public void Mention(long userId, string fullName) => InsertMention($"@**{fullName}|{userId}**");

    /// <summary>在输入框的光标处插入提及的 Markdown，并把焦点移到输入框。</summary>
    public void InsertMention(string markdown)
    {
        if (CanEdit)
            InsertRequested?.Invoke(this, markdown);
    }

    /// <summary>在输入框的光标处插入文字（插件候选回复），并把焦点移到输入框；不会发送。</summary>
    public void InsertText(string text) => InsertMention(text);

    // 编辑消息时输入框中是消息原文：不保存草稿，也不发送“正在输入”
    partial void OnTextChanged(string value)
    {
        if (_loadingText || _closed || !_opened || IsEditingMessage)
            return;
        _draftDirty = true;
        _draftTimer.Stop();
        _draftTimer.Start();
        if (!_programmaticEdit)
            _typing.OnInput(_chat.Key, !string.IsNullOrWhiteSpace(value));
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

    // 图片上传中、引用获取原文中不能发送：正文中还是占位
    private bool CanSend() =>
        HasSendPermission && !IsSending && _imageUploads.Count == 0 && _pendingQuotes == 0
        && Attachments.All(a => a.State != AttachmentState.Uploading)
        && !string.IsNullOrWhiteSpace(Text);

    private bool CanSchedule() => CanSend() && !IsEditingMessage;

    /// <summary>
    /// 发送输入框中的内容。超过服务器长度限制时不发送；
    /// 发送前清空输入区并删除草稿。消息本身在后台发送（本地回显立即出现）。编辑消息时为保存编辑。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private Task SendAsync() => IsEditingMessage ? SaveEditAsync() : SubmitAsync(null);

    /// <summary>定时发送：选择时间后把输入区的内容提交为定时消息，服务器接受后才清空输入区。</summary>
    [RelayCommand(CanExecute = nameof(CanSchedule))]
    private async Task ScheduleAsync()
    {
        if (await DateTimeDialogViewModel.ShowAsync(_chat.Host, "定时发送", "定时发送") is { } at && !_closed)
            await SubmitAsync(at);
    }

    // scheduleAt 为 null 时立即发送，否则在该本地时间定时发送
    private async Task SubmitAsync(DateTime? scheduleAt)
    {
        // 定时发送的时间选择期间内容可能被清空或正在上传
        if (!CanSend() || _session.Mocks(_isSendAllowed && !_chat.UsesUnavailableWildcard(Text), _showToast))
            return;
        var key = _chat.Key;
        var submittedText = Text;
        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        try
        {
            var text = Text.TrimEnd();
            if (_session.Realm.MaxMessageLength > 0 && text.Length > _session.Realm.MaxMessageLength)
            {
                _showToast("消息过长");
                return;
            }
            if (await PluginHost.Instance.FilterOutgoingAsync(key, text, _session) is not { } filtered)
            {
                _showToast("消息已被插件拦截");
                return;
            }
            if (_closed)
                return;
            // 插件可能改写了内容：重新检查
            if (string.IsNullOrWhiteSpace(filtered))
            {
                _showToast("消息已被插件清空");
                return;
            }
            if (_session.Realm.MaxMessageLength > 0 && filtered.Length > _session.Realm.MaxMessageLength)
            {
                _showToast("消息过长");
                return;
            }
            text = filtered;

            if (scheduleAt is { } at)
            {
                // 会话的键在 await 之后取：话题可能在获取引用原文或插件过滤期间被移动
                var scheduledKey = _chat.Key;
                try
                {
                    await _session.Scheduled.CreateAsync(scheduledKey, text, new DateTimeOffset(at).ToUnixTimeSeconds());
                }
                catch (Exception ex)
                {
                    if (_session.IsDisposed)
                        return;
                    Log.Warn($"定时发送失败：{scheduledKey}", ex);
                    _showToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"定时发送失败：{api.Message}" : "定时发送失败");
                    return;
                }
                if (_closed)
                {
                    // 请求期间聊天被关闭，关闭时把内容存成了草稿：已经定时发送，删除提交的那份草稿（之后写了别的内容时保留）
                    if (!_session.IsDisposed && _session.Drafts.Get(scheduledKey)?.Trim() == submittedText.Trim())
                        _session.Forget(_session.Drafts.DeleteAsync(scheduledKey), "删除草稿");
                    return;
                }
                _showToast($"将在 {at:yyyy/MM/dd HH:mm} 发送");
            }

            _loadingText = true;
            Text = "";
            _loadingText = false;
            _draftDirty = false;
            _draftTimer.Stop();
            _typing.Stop();
            Mentions.Close();
            Languages.Close();
            TextReset?.Invoke(this, EventArgs.Empty);
            // 关闭时等待删除完成
            _draftSave = _session.Drafts.DeleteAsync(_chat.Key);
            _session.Forget(_draftSave, "删除草稿");
            if (scheduleAt is null)
                _session.Forget(SendToLatestAsync(text), "发送消息");
        }
        finally
        {
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
        }
    }

    // ---- 在输入框中编辑消息 ----

    /// <summary>
    /// 开始编辑一条消息：输入框中原来的内容暂存起来（草稿立即保存），换成消息的原文。
    /// 已在编辑另一条时先结束那一条。
    /// </summary>
    public void BeginEdit(MessageItemViewModel item, string raw)
    {
        if (_closed)
            return;
        EndEdit(focus: false);
        SaveDraft();
        _typing.Stop();
        Mentions.Close();
        Languages.Close();
        _stashText = Text;
        _editOriginal = raw;
        EditTarget = item;
        item.IsEditing = true;
        _loadingText = true;
        // 多行消息结尾补一个换行，光标在新的一行（比较是否修改时忽略结尾空白）
        Text = MarkdownFormat.WithTrailingLine(raw);
        _loadingText = false;
        TextReset?.Invoke(this, EventArgs.Empty);
        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>正在编辑的消息换成了新的气泡（消息被重新加载）：高亮与预览跟随新气泡。</summary>
    public void RetargetEdit(MessageItemViewModel item)
    {
        if (EditTarget is null || EditTarget == item)
            return;
        EditTarget.IsEditing = false;
        EditTarget = item;
        item.IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => EndEdit();

    // 结束编辑：恢复暂存的输入内容
    private void EndEdit(bool focus = true)
    {
        if (EditTarget is not { } target)
            return;
        target.IsEditing = false;
        EditTarget = null;
        _loadingText = true;
        Text = _stashText;
        _loadingText = false;
        _stashText = "";
        _editOriginal = "";
        Mentions.Close();
        Languages.Close();
        // 编辑期间完成的图片上传改了暂存的内容
        if (_draftDirty && !_closed)
        {
            _draftTimer.Stop();
            _draftTimer.Start();
        }
        TextReset?.Invoke(this, EventArgs.Empty);
        if (focus)
            FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>保存编辑：内容为空或过长时提示；没有修改时直接结束编辑。新内容由 update_message 事件显示。</summary>
    private async Task SaveEditAsync()
    {
        if (EditTarget is not { } target || IsSending || _imageUploads.Count > 0 || _pendingQuotes > 0
            || _session.Mocks(_chat.IsEditAllowed(target), _showToast))
            return;
        var text = Text.TrimEnd();
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
            EndEdit();
            return;
        }

        IsSending = true;
        SendCommand.NotifyCanExecuteChanged();
        try
        {
            await _session.Chat.EditAsync(target.Id, text);
            if (!_closed && EditTarget?.Id == target.Id)
                EndEdit();
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"编辑消息失败：{target.Id}", ex);
            _showToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"编辑失败：{api.Message}" : "编辑失败");
        }
        finally
        {
            IsSending = false;
            SendCommand.NotifyCanExecuteChanged();
            FocusRequested?.Invoke(this, EventArgs.Empty);
        }
    }

    // 源码 / 渲染是全局设置：保存后所有输入框（其它聊天窗口）在 OnLocalSettingChanged 中一起切换
    [RelayCommand]
    private void ToggleSourceMode() => _session.Settings.Local.ComposerSourceMode = !IsSourceMode;

    // 正在查看较早的消息时，本地回显和新消息不会追加到时间线：先回到最新的消息再发送。
    // 会话的键在 await 之后取：话题可能在上传或加载期间被移动
    private async Task SendToLatestAsync(string content)
    {
        await _chat.ShowLatestAsync();
        if (!_session.IsDisposed)
            await _session.Chat.SendAsync(_chat.Key, content, _showToast);
    }

    // ---- 常用语 ----

    /// <summary>全部常用语（Zulip 的 saved snippets），按创建时间先后。</summary>
    public IReadOnlyList<SavedSnippetDto> Snippets => _session.Snippets.All;

    /// <summary>新建常用语：内容为输入框中的文字，标题在对话框中输入。</summary>
    public void ShowNewSnippetDialog()
    {
        var content = Text.Trim();
        if (content.Length == 0)
            return;
        var host = _chat.Host;
        TextInputDialogViewModel dialog = null!;
        dialog = new TextInputDialogViewModel("新建常用语", "常用语标题", "", MaxSnippetTitleLength, async title =>
        {
            host.Dialogs.Remove(dialog);
            if (title is null)
                return;
            try
            {
                await _session.Snippets.CreateAsync(title, content);
                _showToast("已保存为常用语");
            }
            catch (Exception ex)
            {
                if (_session.IsDisposed)
                    return;
                Log.Warn("保存常用语失败", ex);
                _showToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"保存失败：{api.Message}" : "保存失败");
            }
        });
        host.Dialogs.Add(dialog);
    }

    /// <summary>删除常用语（先确认）。列表由 saved_snippets 事件更新。</summary>
    public async Task DeleteSnippetAsync(SavedSnippetDto snippet)
    {
        if (!await _chat.Host.ConfirmAsync($"删除常用语「{snippet.Title}」？", "删除", isDanger: true))
            return;
        try
        {
            await _session.Snippets.DeleteAsync(snippet.Id);
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"删除常用语失败：{snippet.Id}", ex);
            _showToast("删除失败");
        }
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

    /// <summary>选择图片（可多选），立即上传并插入到输入框的光标处。</summary>
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

    /// <summary>添加本地文件（选择、拖放、粘贴）：图片插入正文，其它文件上传后立即发送。</summary>
    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (!HasSendPermission || _session.Mocks(_isSendAllowed, _showToast))
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
        if (!HasSendPermission || _session.Mocks(_isSendAllowed, _showToast))
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

    // 在光标处插入上传占位 [Uploading 名字…]()（与网页版相同），上传完成后换成图片链接。
    // 占位必须唯一：同名图片加上序号
    private void AddImage(string path)
    {
        // 发送中会清空输入框，占位会丢失
        if (IsSending)
            return;
        var name = Path.GetFileName(path).Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal);
        var placeholder = UploadPlaceholder(name);
        for (var k = 2; Text.Contains(placeholder, StringComparison.Ordinal) || _imageUploads.Any(u => u.Placeholder == placeholder); k++)
            placeholder = UploadPlaceholder($"{Path.GetFileNameWithoutExtension(name)} ({k}){Path.GetExtension(name)}");
        var upload = new ImageUpload(path, placeholder);
        _imageUploads.Add(upload);
        InsertRequested?.Invoke(this, placeholder);
        SendCommand.NotifyCanExecuteChanged();
        ScheduleCommand.NotifyCanExecuteChanged();
        _ = UploadImageAsync(upload);
    }

    private static string UploadPlaceholder(string name) => $"[Uploading {name}…]()";

    private async Task UploadImageAsync(ImageUpload upload)
    {
        try
        {
            var result = await _session.Chat.UploadAsync(upload.LocalPath, null, upload.Cancellation.Token);
            if (_closed || _session.IsDisposed)
                return;
            var name = string.IsNullOrEmpty(result.Filename) ? Path.GetFileName(upload.LocalPath) : result.Filename;
            ReplacePlaceholder(upload.Placeholder, LinkMarkdown(name, result.Url));
        }
        catch (OperationCanceledException) when (upload.Cancellation.IsCancellationRequested || _session.IsDisposed)
        {
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn($"上传失败：{upload.LocalPath}", ex);
            ReplacePlaceholder(upload.Placeholder, "");
            _showToast("图片上传失败");
        }
        finally
        {
            _imageUploads.Remove(upload);
            SendCommand.NotifyCanExecuteChanged();
            ScheduleCommand.NotifyCanExecuteChanged();
        }
    }

    // 用户已删掉占位时什么都不做；编辑消息期间占位在暂存的输入内容中
    private void ReplacePlaceholder(string placeholder, string replacement)
    {
        var index = Text.IndexOf(placeholder, StringComparison.Ordinal);
        if (index >= 0)
        {
            _programmaticEdit = true;
            try
            {
                Text = Text.Remove(index, placeholder.Length).Insert(index, replacement);
            }
            finally
            {
                _programmaticEdit = false;
            }
            return;
        }
        index = _stashText.IndexOf(placeholder, StringComparison.Ordinal);
        if (IsEditingMessage && index >= 0)
        {
            _stashText = _stashText.Remove(index, placeholder.Length).Insert(index, replacement);
            _draftDirty = true;
        }
    }

    private async Task UploadAndSendFileAsync(string path)
    {
        var attachment = new PendingAttachment(path);
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

    /// <summary><c>[文件名](地址)</c>；文件名中的方括号去掉（Zulip 没有反斜杠转义，与网页版相同）。</summary>
    internal static string LinkMarkdown(string fileName, string url) =>
        $"[{fileName.Replace("[", "", StringComparison.Ordinal).Replace("]", "", StringComparison.Ordinal)}]({url})";

    // 会话的键在获取原文之后取：话题可能在此期间被移动
    private string QuoteBlock(MessageItemViewModel quote, string raw)
    {
        var key = _chat.Key;
        var channel = key is TopicKey t ? _session.Channels.Get(t.StreamId)?.Name ?? "" : "";
        var topic = key is TopicKey tk ? _session.Conversations.GetTopicName(tk) : "";
        var link = ZulipLinks.MessageLink(_session.Client.RealmUrl, key, _session.SelfId, quote.Id, channel, topic);
        return QuoteMarkdown(quote.Message.SenderName, quote.Message.SenderId, link, raw);
    }

    /// <summary>
    /// 与 Zulip 网页端 compose_reply.ts 引用单条消息的格式相同（使用其简体中文译文 “{username} [发言道]({link_to_message})：”）：
    /// <c>@_**发送者|id** [发言道](消息链接)：</c> 换行后是 quote 围栏代码块，围栏见 <see cref="MarkdownFormat.UnusedFence"/>。
    /// </summary>
    public static string QuoteMarkdown(string senderName, long senderId, string link, string raw)
    {
        var fence = MarkdownFormat.UnusedFence(raw);
        return $"@_**{senderName}|{senderId}** [发言道]({link})：\n{fence}quote\n{raw}\n{fence}";
    }

    // 与网页版的 “[Quoting…]” 相同（简体中文译文）
    private const string QuotingPlaceholder = "[引用…]";

    private void RefreshPermission()
    {
        _isSendAllowed = _chat.Key is not TopicKey t || _session.Channels.Get(t.StreamId) is not { } channel
                         || _session.Permissions.CanSendToChannel(channel);
        HasSendPermission = _session.Shows(_isSendAllowed);
    }

    private void OnSettingChanged(object? sender, string name)
    {
        if (name is "" or "enter_sends")
            OnPropertyChanged(nameof(EnterSends));
    }

    private void OnLocalSettingChanged(object? sender, string key)
    {
        if (key == LocalSettings.ComposerSourceModeKey)
            IsSourceMode = _session.Settings.Local.ComposerSourceMode;
    }

    private void OnPermissionsChanged(object? sender, EventArgs e) => RefreshPermission();

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (_chat.Key is TopicKey t && t.StreamId == streamId)
            RefreshPermission();
    }

    [GeneratedRegex(@"\[Uploading [^\]\n]*…\]\(\) ?")]
    private static partial Regex StaleUploadPattern();

    private sealed class ImageUpload(string localPath, string placeholder)
    {
        public string LocalPath { get; } = localPath;
        public string Placeholder { get; } = placeholder;
        public CancellationTokenSource Cancellation { get; } = new();
    }
}
