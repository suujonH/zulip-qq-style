using System;
using System.Collections.Generic;
using System.Linq;

namespace ZulipQQ.Core;

/// <summary>Notification Bot 消息的显示方式。</summary>
public enum SystemNoticeKind
{
    /// <summary>不是 Notification Bot 的消息，按普通消息显示。</summary>
    None,

    /// <summary>话题中的事件提示：在聊天中显示为居中灰色小字。</summary>
    Inline,

    /// <summary>公告或机器人私信：只在“系统通知”中显示。</summary>
    Notice,

    /// <summary>消息提醒到时间：只在“消息提醒”窗口中显示。</summary>
    Reminder,
}

/// <summary>系统通知的分类（筛选用）。</summary>
public enum SystemNoticeCategory
{
    Other,
    NewChannels,
    Signups,
    ZulipUpdates,
    Moderation,
    Direct,
}

/// <summary>按发送者、频道和话题名区分 Notification Bot 的消息。</summary>
public static class SystemNoticeRules
{
    // 服务器按组织默认语言翻译话题名；zh_Hans 的 "moderation" 译文本身就是“{name} 提到了您：”
    private static readonly string[] NewChannelsTopics = ["new channels", "新频道"];
    private static readonly string[] SignupsTopics = ["signups", "注册"];
    private static readonly string[] ZulipUpdatesTopics = ["Zulip updates"];
    private static readonly string[] ModerationSuffixes = [" moderation", " 提到了您："];

    /// <summary>提醒私信的开头（服务器只有英文，见 zerver/actions/reminders.py）。</summary>
    public const string ReminderMarker = "You requested a reminder for the following";

    /// <summary>
    /// 不是 Notification Bot 发的为 <see cref="SystemNoticeKind.None"/>；私信为系统通知或提醒；
    /// 公告频道中的固定公告话题为系统通知；其它频道消息为灰色提示条。
    /// </summary>
    public static SystemNoticeKind Classify(long? botId, IReadOnlySet<long> announcementChannelIds,
        long senderId, long? streamId, string topic, string html)
    {
        if (botId is null || senderId != botId)
            return SystemNoticeKind.None;
        if (streamId is null)
            return html.Contains(ReminderMarker, StringComparison.Ordinal) ? SystemNoticeKind.Reminder : SystemNoticeKind.Notice;
        return announcementChannelIds.Contains(streamId.Value) && IsAnnouncementTopic(topic)
            ? SystemNoticeKind.Notice
            : SystemNoticeKind.Inline;
    }

    /// <summary>服务器发公告时使用的话题名；频道只允许空话题时公告发在空话题。</summary>
    public static bool IsAnnouncementTopic(string topic) =>
        topic.Length == 0 || Matches(NewChannelsTopics, topic) || Matches(SignupsTopics, topic)
        || Matches(ZulipUpdatesTopics, topic) || IsModeration(topic);

    public static SystemNoticeCategory CategoryOf(long? streamId, string topic)
    {
        if (streamId is null)
            return SystemNoticeCategory.Direct;
        if (Matches(NewChannelsTopics, topic))
            return SystemNoticeCategory.NewChannels;
        if (Matches(SignupsTopics, topic))
            return SystemNoticeCategory.Signups;
        if (Matches(ZulipUpdatesTopics, topic))
            return SystemNoticeCategory.ZulipUpdates;
        return IsModeration(topic) ? SystemNoticeCategory.Moderation : SystemNoticeCategory.Other;
    }

    public static string CategoryText(SystemNoticeCategory category) => category switch
    {
        SystemNoticeCategory.NewChannels => "新频道",
        SystemNoticeCategory.Signups => "新成员",
        SystemNoticeCategory.ZulipUpdates => "Zulip 更新",
        SystemNoticeCategory.Moderation => "举报",
        SystemNoticeCategory.Direct => "通知",
        _ => "公告",
    };

    private static bool Matches(string[] names, string topic) =>
        names.Any(n => string.Equals(n, topic, StringComparison.OrdinalIgnoreCase));

    private static bool IsModeration(string topic) =>
        ModerationSuffixes.Any(s => topic.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}
