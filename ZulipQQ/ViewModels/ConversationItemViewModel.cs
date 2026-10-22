using System;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>生成会话列表项所需的数据。</summary>
/// <param name="LastMessageTime">Unix 秒；0 表示没有消息。</param>
/// <param name="Preview">已带“发送者：”前缀的预览文字。</param>
public sealed record ConversationSnapshot(
    ConversationKey Key,
    string Title,
    long LastMessageTime,
    string Preview,
    string? Draft,
    int UnreadCount,
    ConversationNotice Notice,
    DndLevel Level,
    bool IsPinned);

/// <summary>会话列表中的一项，负责把 <see cref="ConversationSnapshot"/> 转成界面显示用的文字。数据变化时就地更新。</summary>
public partial class ConversationItemViewModel(ConversationKey key) : ViewModelBase
{
    private const int MaxUnreadDisplay = 99;

    public ConversationKey Key { get; } = key;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial string TimeText { get; private set; } = "";

    [ObservableProperty]
    public partial bool HasDraft { get; private set; }

    [ObservableProperty]
    public partial string PreviewText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNotice))]
    public partial string? NoticeText { get; private set; }

    public bool HasNotice => NoticeText is not null;

    [ObservableProperty]
    public partial string UnreadText { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMutedIcon))]
    public partial bool HasUnread { get; private set; }

    /// <summary>免打扰的会话未读数显示为灰色。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowMutedIcon))]
    public partial bool IsMuted { get; private set; }

    /// <summary>免打扰且没有未读时，在未读位置显示免打扰图标。</summary>
    public bool ShowMutedIcon => IsMuted && !HasUnread;

    [ObservableProperty]
    public partial bool IsPinned { get; private set; }

    public void Update(ConversationSnapshot s, DateTime now)
    {
        Title = s.Title;
        TimeText = s.LastMessageTime > 0 ? Core.TimeText.ListTime(Core.TimeText.FromUnix(s.LastMessageTime), now) : "";
        HasDraft = !string.IsNullOrEmpty(s.Draft);
        PreviewText = HasDraft ? s.Draft! : s.Preview;

        // 有草稿时 QQ 只显示草稿，不显示红色提示；屏蔽的会话不显示提示和未读
        var blocked = s.Level == DndLevel.Blocked;
        NoticeText = HasDraft || blocked ? null : s.Notice switch
        {
            ConversationNotice.AtAll => "[@全体成员]",
            ConversationNotice.AtMe => "[有人@我]",
            ConversationNotice.NewFile => "[有新文件]",
            _ => null,
        };

        var unread = blocked ? 0 : s.UnreadCount;
        UnreadText = unread > MaxUnreadDisplay
            ? $"{MaxUnreadDisplay}+"
            : unread.ToString(CultureInfo.InvariantCulture);
        HasUnread = unread > 0;
        IsMuted = s.Level != DndLevel.Notify;
        IsPinned = s.IsPinned;
    }
}
