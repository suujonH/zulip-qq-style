using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Media;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 托盘提醒列表（托盘图标闪烁时鼠标悬停显示）的一项：有未读的会话，或消息提醒（未读的已到时提醒、即将到时的提醒）。
/// </summary>
public sealed class TrayMessageItemViewModel
{
    // 即将到时的提醒：24 小时内
    private static readonly TimeSpan UpcomingWindow = TimeSpan.FromHours(24);

    private TrayMessageItemViewModel(string title, string preview, Action activate)
    {
        Title = title;
        Preview = preview;
        Activate = activate;
    }

    public string Title { get; }

    /// <summary>多个账号在线时显示收到消息的账号；否则为 null。</summary>
    public string? AccountName { get; private init; }

    public bool HasAccountName => AccountName is not null;

    /// <summary>预览前的红色提示：[有人@我]、[提醒词]、[@全体成员]、[有新文件]。</summary>
    public string? NoticeText { get; private init; }

    public bool HasNotice => NoticeText is not null;

    public string Preview { get; }

    public string? AvatarUrl { get; private init; }

    public string AvatarText { get; private init; } = "";

    public IBrush? AvatarFill { get; private init; }

    public string UnreadText { get; private init; } = "";

    public bool HasUnread { get; private init; }

    /// <summary>消息提醒的倒计时：还有X分钟 / 已过X分钟；会话为 null。</summary>
    public string? CountdownText { get; private init; }

    public bool HasCountdown => CountdownText is not null;

    /// <summary>点击：打开会话或消息提醒窗口。</summary>
    public Action Activate { get; }

    // 排序：先是消息提醒（按提醒时间），再是会话（最新的在前）
    private int Group { get; init; }

    private long SortTime { get; init; }

    /// <summary>
    /// 一个账号的列表项。会话：允许提醒的会话中的未读，以及免打扰会话中提醒过（@我、@全体、提醒词）的未读；
    /// 消息提醒：未读的已到时提醒、24 小时内将到时的提醒。
    /// </summary>
    public static IEnumerable<TrayMessageItemViewModel> Build(ZulipSession session, string? accountName, DateTime now,
        Action<ConversationKey, long?> openChat, Action<ReminderTab> openReminders)
    {
        var mutedAlerts = session.Notifications.UnreadMutedAlertKeys();
        foreach (var key in session.Unread.Keys.ToList())
        {
            if (session.Conversations.GetEffectiveLevel(key) != DndLevel.Notify && !mutedAlerts.Contains(key))
                continue;
            if (session.Conversations.Get(key) is not { } entry)
                continue;
            var title = session.Conversations.GetTitle(key);
            var (avatarUrl, avatarText, avatarColor) = key switch
            {
                TopicKey t => (null, title, session.Channels.Get(t.StreamId)?.Color),
                DmKey { IsSelfDm: true } => (session.Users.Get(session.SelfId)?.AvatarUrl, title, null),
                DmKey { OtherUserIds.Length: 1 } d => (session.Users.Get(d.OtherUserIds[0])?.AvatarUrl, title, null),
                _ => ((string?)null, "群", MessagesPageViewModel.GroupDmAvatarColor),
            };
            var multiParty = key is TopicKey or DmKey { IsGroupDm: true };
            var preview = multiParty && entry.LastMessageId > 0 && entry.LastSenderId != session.SelfId
                ? $"{session.Contacts.DisplayName(entry.LastSenderId)}：{entry.LastPreview}"
                : entry.LastPreview;
            var unread = session.Unread.Count(key);
            var (noticeText, focusId) = NoticeOf(session, key);
            yield return new TrayMessageItemViewModel(session.Conversations.GetDisplayTitle(key), preview, () => openChat(key, focusId))
            {
                AccountName = accountName,
                NoticeText = noticeText,
                AvatarUrl = avatarUrl,
                AvatarText = avatarText,
                AvatarFill = ConversationItemViewModel.BrushOf(avatarColor),
                UnreadText = unread > 99 ? "99+" : unread.ToString(CultureInfo.InvariantCulture),
                HasUnread = unread > 0,
                Group = 1,
                SortTime = -entry.LastMessageTime,
            };
        }

        foreach (var e in session.SystemNotices.DeliveredReminders.Where(e => session.SystemNotices.IsUnread(e.Id)))
            yield return Reminder(ZulipHtml.ToPreview(SystemNoticeText.LocalizeReminder(e.Html)), e.Timestamp, () => openReminders(ReminderTab.Delivered));
        foreach (var r in session.Reminders.Pending.Where(r => !r.Failed
                     && TimeText.FromUnix(r.ScheduledDeliveryTimestamp) - now <= UpcomingWindow))
        {
            var content = ZulipHtml.ToPreview(SystemNoticeText.LocalizeReminder(r.RenderedContent.Length > 0 ? r.RenderedContent : r.Content));
            yield return Reminder(content, r.ScheduledDeliveryTimestamp, () => openReminders(ReminderTab.Pending));
        }

        TrayMessageItemViewModel Reminder(string preview, long timestamp, Action activate) =>
            new("消息提醒", preview.Length > 0 ? preview : "你设置的消息提醒", activate)
            {
                AccountName = accountName,
                AvatarText = "提醒",
                CountdownText = TimeText.Countdown(TimeText.FromUnix(timestamp), now),
                Group = 0,
                SortTime = timestamp,
            };
    }

    /// <summary>所有账号的列表项排序：先是消息提醒（按提醒时间），再是会话（最新的在前）。</summary>
    public static List<TrayMessageItemViewModel> Sort(IEnumerable<TrayMessageItemViewModel> items) =>
        items.OrderBy(i => i.Group).ThenBy(i => i.SortTime).ToList();

    // 红色提示与点击后定位的消息（该类中最新的一条未读），优先级 @我 > 提醒词 > @全体 > 新文件；
    // 新文件与没有提示时不定位（打开会话的底部）
    private static (string? Text, long? FocusId) NoticeOf(ZulipSession session, ConversationKey key)
    {
        var unread = session.Unread;
        var notice = unread.GetNotice(key);
        if (notice == ConversationNotice.AtMe)
            return ("[有人@我]", unread.LatestMention(key, MentionKind.Personal));
        if (unread.LatestAlertWord(key) is { } alertWord)
            return ("[提醒词]", alertWord);
        return notice switch
        {
            ConversationNotice.AtAll => ("[@全体成员]", unread.LatestMention(key, MentionKind.Wildcard)),
            ConversationNotice.NewFile => ("[有新文件]", null),
            _ => (null, null),
        };
    }
}
