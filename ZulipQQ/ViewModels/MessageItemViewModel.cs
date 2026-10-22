using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>聊天中的一条消息（气泡）。数据变化时调用 <see cref="Refresh"/> 重新读取。</summary>
public partial class MessageItemViewModel : ViewModelBase
{
    private readonly ChatViewModel _chat;
    private readonly ZulipSession _session;

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

    [ObservableProperty]
    public partial string ContentHtml { get; private set; } = "";

    /// <summary>“/me” 消息的发送者名字（内容前显示），普通消息为 null。</summary>
    [ObservableProperty]
    public partial string? MeSender { get; private set; }

    /// <summary>只有图片 / 文件：不显示气泡背景。</summary>
    [ObservableProperty]
    public partial bool IsBare { get; private set; }

    [ObservableProperty]
    public partial bool IsEdited { get; private set; }

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

    /// <summary>点击气泡后在昵称后显示精确时间，气泡变为选中色。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreciseTimeText))]
    public partial bool IsTimeShown { get; set; }

    public string PreciseTimeText =>
        TimeText.PreciseTime(TimeText.FromUnix(Message.Timestamp), System.DateTime.Now);

    /// <summary>跳转到这条消息时短暂高亮。</summary>
    [ObservableProperty]
    public partial bool IsFlashing { get; set; }

    public void Refresh()
    {
        var user = _session.Users.Get(Message.SenderId);
        SenderName = user is null ? Message.SenderName : _session.Contacts.DisplayName(Message.SenderId);
        AvatarUrl = user?.AvatarUrl ?? Message.AvatarUrl;
        MeSender = Message.IsMeMessage ? SenderName : null;
        if (ContentHtml != Message.ContentHtml)
        {
            ContentHtml = Message.ContentHtml;
            IsBare = ZulipHtml.IsAttachmentOnly(ContentHtml);
        }
        IsEdited = Message.LastEditTimestamp is not null;
        IsPending = Message.SendState == MessageSendState.Pending;
        IsFailed = Message.SendState == MessageSendState.Failed;
        IsStarred = Message.IsStarred;

        // 同一表情合并为一项，按第一次出现的顺序
        Reactions = Message.Reactions
            .GroupBy(r => (r.ReactionType, r.EmojiCode))
            .Select(g => new ReactionViewModel(this, g.First().EmojiName, g.Key.EmojiCode, g.Key.ReactionType,
                g.Select(r => r.UserId).ToList(), _session))
            .ToList();
        ReactionCount = Reactions.Count;
    }

    internal Task ToggleReactionAsync(ReactionViewModel reaction) => _chat.ToggleReactionAsync(this, reaction);

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
