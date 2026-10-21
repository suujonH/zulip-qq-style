using System;
using System.Globalization;
using ZulipQQ.Models;

namespace ZulipQQ.ViewModels;

/// <summary>会话列表中的一项，负责把 <see cref="Conversation"/> 转成界面显示用的文字。</summary>
public class ConversationItemViewModel : ViewModelBase
{
    private const int MaxUnreadDisplay = 99;

    public ConversationItemViewModel(Conversation conversation, DateTime now)
    {
        Conversation = conversation;
        TimeText = Core.TimeText.ListTime(conversation.LastMessageTime, now);
        HasDraft = !string.IsNullOrEmpty(conversation.Draft);

        if (HasDraft)
            PreviewText = conversation.Draft!;
        else if (conversation.LastSender is null)
            PreviewText = conversation.LastMessage;
        else
            PreviewText = $"{conversation.LastSender}：{conversation.LastMessage}";

        // 有草稿时 QQ 只显示草稿，不显示红色提示
        NoticeText = HasDraft ? null : conversation.Notice switch
        {
            ConversationNotice.AtAll => "[@全体成员]",
            ConversationNotice.AtMe => "[有人@我]",
            ConversationNotice.NewFile => "[有新文件]",
            _ => null,
        };

        UnreadText = conversation.UnreadCount > MaxUnreadDisplay
            ? $"{MaxUnreadDisplay}+"
            : conversation.UnreadCount.ToString(CultureInfo.InvariantCulture);
    }

    public Conversation Conversation { get; }

    public string Title => Conversation.Title;

    public string TimeText { get; }

    public bool HasDraft { get; }

    public string PreviewText { get; }

    public string? NoticeText { get; }

    public bool HasNotice => NoticeText is not null;

    public string UnreadText { get; }

    public bool HasUnread => Conversation.UnreadCount > 0;

    /// <summary>免打扰的会话未读数显示为灰色。</summary>
    public bool IsMuted => Conversation.IsMuted;

    /// <summary>免打扰且没有未读时，在未读位置显示免打扰图标。</summary>
    public bool ShowMutedIcon => IsMuted && !HasUnread;

    public bool IsPinned => Conversation.IsPinned;
}
