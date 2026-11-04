using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Plugins;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>聊天中的一条消息（气泡）。数据变化时调用 <see cref="Refresh"/> 重新读取。</summary>
public partial class MessageItemViewModel : ViewModelBase
{
    private readonly ChatViewModel _chat;
    private readonly ZulipSession _session;
    private string _versionHtml = "";

    public MessageItemViewModel(ChatViewModel chat, ZulipSession session, ChatMessage message)
    {
        _chat = chat;
        _session = session;
        Message = message;
        IsOwn = message.SenderId == session.SelfId;
        Refresh();
    }

    public ChatMessage Message { get; }

    public long Id => Message.Id;

    public bool IsOwn { get; }

    /// <summary>一对一私聊（与自己的私聊）不显示昵称，与 QQ 一致。</summary>
    public bool ShowSenderName => _chat.ShowsSenderNames;

    [ObservableProperty]
    public partial string SenderName { get; private set; } = "";

    [ObservableProperty]
    public partial string? AvatarUrl { get; private set; }

    /// <summary>显示的内容：编辑过的消息为当前选择的版本，默认为最新版本。</summary>
    [ObservableProperty]
    public partial string ContentHtml { get; private set; } = "";

    /// <summary>“/me” 消息的发送者名字（内容前显示），普通消息为 null。</summary>
    [ObservableProperty]
    public partial string? MeSender { get; private set; }

    /// <summary>只有图片 / 文件：不显示气泡背景。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBare))]
    public partial bool IsBare { get; private set; }

    /// <summary>发送者被屏蔽：内容替换为“已屏蔽的消息（点击查看）”，点击后在本次打开期间显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBare), nameof(ShowsContent), nameof(ShowsWidget))]
    public partial bool IsMutedHidden { get; private set; }

    public bool ShowsBare => IsBare && !IsMutedHidden && Widget is null;

    /// <summary>显示消息内容（没有被屏蔽，也不是投票 / 待办）。</summary>
    public bool ShowsContent => !IsMutedHidden && Widget is null;

    /// <summary>投票 / 待办消息：内容（“/poll …”）换成可以投票、勾选的卡片。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsBare), nameof(ShowsContent), nameof(ShowsWidget))]
    public partial WidgetCardViewModel? Widget { get; private set; }

    public bool ShowsWidget => !IsMutedHidden && Widget is not null;

    /// <summary>
    /// 卡片中显示添加选项 / 任务的输入框。放在消息上而不是卡片上：卡片在每次 submessage 事件后重建，输入不能丢。
    /// </summary>
    [ObservableProperty]
    public partial bool IsAddingWidgetRow { get; set; }

    [ObservableProperty]
    public partial string NewWidgetRowText { get; set; } = "";

    /// <summary>
    /// 上一次添加时使用的序号。服务器的 submessage 事件到达前再次添加，
    /// 从子消息算出的序号还是旧的，用它避免重复。
    /// </summary>
    internal long LastWidgetIndex { get; set; }

    /// <summary>正在下方的输入框中编辑：气泡变为选中色，内容照常显示。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    public partial bool IsEditing { get; set; }

    /// <summary>左键按下气泡后变为选中色，保留到按下其它地方。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    public partial bool IsSelected { get; set; }

    /// <summary>右键按住气泡时变为选中色，抬起后恢复。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted))]
    public partial bool IsPressed { get; set; }

    /// <summary>气泡显示为选中色：被选中、右键按住或正在编辑。</summary>
    public bool IsHighlighted => IsSelected || IsPressed || IsEditing;

    private bool _revealed;

    /// <summary>编辑过内容的消息的版本总数（含最新版本，本地没有旧版本时为 1）；没有编辑过时为 0。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVersions), nameof(VersionText), nameof(CanShowPreviousVersion), nameof(CanShowNextVersion))]
    public partial int VersionCount { get; private set; }

    /// <summary>当前显示的版本，从 1（原始内容）到 <see cref="VersionCount"/>（最新版本）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VersionText), nameof(CanShowPreviousVersion), nameof(CanShowNextVersion))]
    public partial int ShownVersion { get; private set; }

    /// <summary>编辑过的消息在气泡下方显示“版本 / 总数”和左右切换箭头（显示了即表示编辑过）。</summary>
    public bool HasVersions => VersionCount > 0;

    public string VersionText => $"{ShownVersion}/{VersionCount}";

    public bool CanShowPreviousVersion => ShownVersion > 1;

    public bool CanShowNextVersion => ShownVersion < VersionCount;

    [ObservableProperty]
    public partial bool IsPending { get; private set; }

    [ObservableProperty]
    public partial bool IsFailed { get; private set; }

    [ObservableProperty]
    public partial bool IsStarred { get; private set; }

    [ObservableProperty]
    public partial IReadOnlyList<ReactionViewModel> Reactions { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReactions))]
    public partial int ReactionCount { get; private set; }

    public bool HasReactions => ReactionCount > 0;

    /// <summary>已启用的插件附加在这条消息下方的内容（与消息原文分开保存）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAnnotations))]
    public partial IReadOnlyList<PluginAnnotationViewModel> Annotations { get; private set; } = [];

    public bool HasAnnotations => Annotations.Count > 0;

    /// <summary>
    /// 鼠标在气泡上停留 1 秒后显示精确时间（与是否选中无关）：显示昵称时在昵称后，否则在气泡上方预留的一行中；
    /// 都不改变气泡的位置。
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreciseTimeText))]
    public partial bool IsTimeShown { get; set; }

    public string PreciseTimeText =>
        TimeText.PreciseTime(TimeText.FromUnix(Message.Timestamp), System.DateTime.Now, _session.Settings.Zulip.TwentyFourHourTime);

    /// <summary>时间格式（12 / 24 小时制）变化后重新取精确时间。</summary>
    public void RefreshTimeText() => OnPropertyChanged(nameof(PreciseTimeText));

    /// <summary>一对一私聊中自己最后一条消息下方的“已读” / “未读”；其它消息为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadStateUnread))]
    public partial string? ReadStateText { get; set; }

    public bool IsReadStateUnread => ReadStateText == ChatViewModel.UnreadStateText;

    /// <summary>多选模式下是否勾选。</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    /// <summary>跨气泡选择中（从一条消息的文字拖动到其它消息）：整条消息选中，气泡变为选中色。</summary>
    [ObservableProperty]
    public partial bool IsRangeSelected { get; set; }

    /// <summary>跨气泡复制时这条消息的文字：第一行为“名字: 时间”，下一行为内容（已屏蔽的消息不复制内容）。</summary>
    public string RangeCopyText => $"{SenderName}: {PreciseTimeText}\n{(IsMutedHidden ? "" : ZulipHtml.ToPlainText(ContentHtml))}";

    /// <summary>跳转到这条消息时短暂高亮。</summary>
    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    public void Refresh()
    {
        var user = _session.Users.Get(Message.SenderId);
        SenderName = user is null ? Message.SenderName : _session.Contacts.DisplayName(Message.SenderId);
        AvatarUrl = user?.AvatarUrl ?? Message.AvatarUrl;
        MeSender = Message.IsMeMessage ? SenderName : null;
        var previous = Message.PreviousContents;
        VersionCount = Message.LastEditTimestamp is not null || previous.Count > 0 ? previous.Count + 1 : 0;
        // 记录的版本超出范围（重新加载后历史变少）时显示最新版本
        ShownVersion = _session.Messages.GetShownVersion(Message.Id) is { } shown && shown >= 1 && shown < VersionCount
            ? shown
            : VersionCount;
        // 实际显示的内容在 RefreshAnnotations 中决定（插件可能临时替换最新版本的显示）
        _versionHtml = ShownVersion < VersionCount ? previous[ShownVersion - 1] : Message.ContentHtml;
        IsPending = Message.SendState == MessageSendState.Pending;
        IsFailed = Message.SendState == MessageSendState.Failed;
        IsStarred = Message.IsStarred;
        IsMutedHidden = !IsOwn && !_revealed && _session.Users.IsMuted(Message.SenderId);
        Widget = WidgetParser.Parse(Message.Submessages, Message.SenderId) is { } widget
            ? WidgetCardViewModel.Create(widget, this, _session)
            : null;

        // 同一表情合并为一项，按第一次出现的顺序
        Reactions = Message.Reactions
            .GroupBy(r => (r.ReactionType, r.EmojiCode))
            .Select(g => new ReactionViewModel(this, g.First().EmojiName, g.Key.EmojiCode, g.Key.ReactionType,
                g.Select(r => r.UserId).ToList(), _session))
            .ToList();
        ReactionCount = Reactions.Count;
        RefreshAnnotations();
    }

    /// <summary>
    /// 重新读取插件附加的内容（停用的插件不显示），并决定气泡显示的内容：显示最新版本时，
    /// 第一个有 render_message 权限的插件的 replace 临时替换显示，用户可以切回原文。
    /// </summary>
    public void RefreshAnnotations()
    {
        var accountId = _session.Account.Id;
        var store = PluginAnnotationStore.Instance;
        var visible = store.Get(accountId, Message.Id)
            .Select(a => (Annotation: a, Plugin: PluginHost.Instance.Plugins.FirstOrDefault(p => p.Id == a.PluginId)))
            .Where(x => x.Plugin is { Enabled: true })
            .ToList();

        var render = ShownVersion == VersionCount
            ? visible.FirstOrDefault(x => x.Annotation.Replace.Length > 0 && x.Plugin!.HasPermission("render_message"))
            : default;
        RenderedBy = render.Plugin?.Name;
        IsOriginalShown = store.IsOriginalShown(accountId, Message.Id);
        var html = render.Plugin is not null && !IsOriginalShown ? PlainTextHtml(render.Annotation.Replace) : _versionHtml;
        if (ContentHtml != html)
        {
            ContentHtml = html;
            IsBare = ZulipHtml.IsAttachmentOnly(ContentHtml);
        }

        Annotations = visible.Where(x => x.Annotation.HasCard)
            .Select(x => new PluginAnnotationViewModel(x.Plugin!, x.Annotation, accountId, Message.Id, _chat.ShowToast,
                _chat.Composer.InsertText))
            .ToList();
    }

    /// <summary>临时替换显示的插件名；没有替换时为 null。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRender), nameof(RenderToggleText))]
    public partial string? RenderedBy { get; private set; }

    /// <summary>插件替换了显示内容，用户选择了显示原文。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RenderToggleText))]
    public partial bool IsOriginalShown { get; private set; }

    public bool HasRender => RenderedBy is not null;

    public string RenderToggleText => IsOriginalShown ? $"显示“{RenderedBy}”的内容" : "显示原文";

    [RelayCommand]
    private void ToggleRender() =>
        PluginAnnotationStore.Instance.SetOriginalShown(_session.Account.Id, Message.Id, !IsOriginalShown);

    // 插件给出的纯文字：空行分段，单个换行保留
    private static string PlainTextHtml(string text) =>
        string.Concat(text.Replace("\r\n", "\n").Split("\n\n", StringSplitOptions.RemoveEmptyEntries)
            .Select(p => "<p>" + System.Net.WebUtility.HtmlEncode(p.Trim('\n')).Replace("\n", "<br>") + "</p>"));

    /// <summary>点击已屏蔽的消息：显示原内容。</summary>
    public void Reveal()
    {
        _revealed = true;
        IsMutedHidden = false;
    }

    internal Task ToggleReactionAsync(ReactionViewModel reaction) => _chat.ToggleReactionAsync(this, reaction);

    internal Task ActivateWidgetRowAsync(WidgetRowViewModel row) => _chat.ActivateWidgetRowAsync(this, row);

    /// <summary>卡片右上角“+”：打开 / 关闭添加选项 / 任务的输入框。</summary>
    [RelayCommand]
    private void ToggleAddWidgetRow()
    {
        NewWidgetRowText = "";
        IsAddingWidgetRow = !IsAddingWidgetRow;
    }

    [RelayCommand]
    private void CancelAddWidgetRow() => IsAddingWidgetRow = false;

    /// <summary>回车：添加输入的选项 / 任务；成功后关闭输入框，失败时保留输入。</summary>
    [RelayCommand]
    private async Task SubmitWidgetRowAsync()
    {
        var text = NewWidgetRowText.Trim();
        if (text.Length == 0)
        {
            IsAddingWidgetRow = false;
            return;
        }
        if (await _chat.AddWidgetRowAsync(this, text))
        {
            NewWidgetRowText = "";
            IsAddingWidgetRow = false;
        }
    }

    /// <summary>显示上一个（更早的）版本。</summary>
    [RelayCommand]
    private void ShowPreviousVersion()
    {
        if (CanShowPreviousVersion)
            _session.Messages.SetShownVersion(Message.Id, ShownVersion - 1);
    }

    /// <summary>显示下一个（更新的）版本；到最新版本时不再记录。</summary>
    [RelayCommand]
    private void ShowNextVersion()
    {
        if (CanShowNextVersion)
            _session.Messages.SetShownVersion(Message.Id, ShownVersion + 1 == VersionCount ? null : ShownVersion + 1);
    }

    /// <summary>点击发送失败的红色感叹号：重发。</summary>
    [RelayCommand]
    private void Retry() => _chat.Retry(this);
}

/// <summary>气泡下方的一个表情回应：表情、人数，自己回应过时高亮。</summary>
public partial class ReactionViewModel(
    MessageItemViewModel owner, string name, string code, string type, IReadOnlyList<long> userIds, ZulipSession session)
    : ViewModelBase
{
    public string Name { get; } = name;
    public string Code { get; } = code;
    public string Type { get; } = type;

    /// <summary>组织自定义表情的图片；Unicode 表情为 null。</summary>
    public string? ImageUrl { get; } =
        type == EmojiEntry.RealmType ? session.Emoji.Catalog.TryGet(name) is { Type: EmojiEntry.RealmType } e ? e.ImageUrl : null : null;

    public bool HasImage => ImageUrl is not null;

    /// <summary>Unicode 表情显示字符；没有图片的其它表情显示为 :名称:。</summary>
    public string EmojiText { get; } =
        (type == EmojiEntry.UnicodeType ? EmojiCatalog.UnicodeFromCode(code) : null) ?? $":{name}:";

    public int Count { get; } = userIds.Count;

    public bool IsMine { get; } = userIds.Contains(session.SelfId);

    public string ToolTipText { get; } = string.Join("、", userIds.Select(session.Contacts.DisplayName)) + $" 回应了 :{name}:";

    [RelayCommand]
    private Task ToggleAsync() => owner.ToggleReactionAsync(this);
}
