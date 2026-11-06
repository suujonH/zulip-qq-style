using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using ZulipQQ.Core;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>一次新消息提醒：是否弹出通知、是否播放提示音，以及通知显示的内容。</summary>
/// <param name="AvatarColor">文字头像的背景色（#RRGGBB）；为 null 时按文字取色板颜色。</param>
/// <param name="Slot">通知槽位：同一槽位的新通知替换旧通知。普通会话各占一个槽位，免打扰的会话共用一个。</param>
/// <param name="IsReminder">消息提醒到时间：点击通知打开消息提醒窗口，而不是会话。</param>
/// <param name="MessageId">触发通知的消息：点击通知打开会话并定位到它；为 null（模拟的消息）时打开会话的底部。</param>
public sealed record NotificationRequest(
    ConversationKey Key,
    string Title,
    string Body,
    string? AvatarUrl,
    string AvatarText,
    string? AvatarColor,
    bool Popup,
    ConversationKey Slot,
    bool IsReminder = false,
    long? MessageId = null);

/// <summary>
/// 新消息提醒：按免打扰级别和 Zulip 的通知设置决定是否弹窗、是否播放提示音。
/// 弹窗与任务栏闪烁由界面（App）订阅 <see cref="Requested"/> 处理。只在 UI 线程上使用。
/// </summary>
public sealed class NotificationService(ZulipSession session)
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x00010000;

    // 免打扰会话中提醒过的消息（@我、@全体、提醒词）；它们不计入未读总数，托盘闪烁按它们是否已读决定何时停止
    private readonly HashSet<long> _mutedAlerts = [];

    /// <summary>需要提醒（任务栏闪烁；<see cref="NotificationRequest.Popup"/> 为 true 时同时弹窗）。</summary>
    public event EventHandler<NotificationRequest>? Requested;

    /// <summary>
    /// 新消息：自己发的、已读的（如服务器代为标记已读的系统消息）、发送者被屏蔽、
    /// 会话为“屏蔽群消息”、正在前台查看该会话时不提醒。
    /// 免打扰（接收不提醒、群助手、收起的已解决话题）只是本地降级：只有 @我、@全体、提醒词才提醒，
    /// 且这些会话共用一个通知（与 QQ 一致）。
    /// 其余情况总是请求闪烁任务栏；是否弹窗、是否有声音按 Zulip 的设置决定。
    /// </summary>
    public void OnNewMessage(ChatMessage m, IReadOnlyList<string> flags)
    {
        // Notification Bot 在话题中的事件提示不提醒
        if (m.Notice != SystemNoticeKind.None
            || m.SenderId == session.SelfId
            || Contains(flags, "read")
            || session.Users.IsMuted(m.SenderId)
            || session.Registry.IsActivelyViewing(m.Key))
            return;

        var level = session.Conversations.GetEffectiveLevel(m.Key);
        if (level == DndLevel.Blocked)
            return;
        var muted = level != DndLevel.Notify;
        if ((muted ? DecideMention(flags) : Decide(m.Key, flags)) is not (var popup, var sound))
            return;
        if (muted)
            _mutedAlerts.Add(m.Id);
        // 静音时只闪烁任务栏：不响提示音、不弹横幅。提示音选择“无”时不播放任何提示音（与 Zulip 一致）
        var silenced = session.Settings.Local.NotificationMuted;
        if (sound && !silenced && session.Settings.Zulip.NotificationSound != "none")
            PlaySound("Notification.IM", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        popup &= !silenced;
        Requested?.Invoke(this, Build(m, popup, muted ? ConversationKey.Helper : m.Key));
    }

    /// <summary>消息提醒到时间：不受会话免打扰影响，按私聊的通知设置弹窗和响铃。</summary>
    public void OnReminder(ChatMessage m)
    {
        var z = session.Settings.Zulip;
        var silenced = session.Settings.Local.NotificationMuted;
        if (z.EnableSounds && !silenced && z.NotificationSound != "none")
            PlaySound("Notification.IM", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        var popup = z.EnableDesktopNotifications && !silenced;
        var avatar = session.Users.Get(m.SenderId)?.AvatarUrl ?? m.AvatarUrl;
        Requested?.Invoke(this, new NotificationRequest(m.Key, "消息提醒", "你设置的消息提醒到时间了", avatar, "提醒", null,
            popup, m.Key, IsReminder: true));
    }

    /// <summary>免打扰会话中是否还有提醒过、尚未读的消息。</summary>
    public bool HasUnreadMutedAlerts()
    {
        _mutedAlerts.RemoveWhere(id => !session.Unread.IsUnread(id));
        return _mutedAlerts.Count > 0;
    }

    /// <summary>免打扰会话中提醒过、尚未读的消息所在的会话。</summary>
    public IReadOnlySet<ConversationKey> UnreadMutedAlertKeys()
    {
        _mutedAlerts.RemoveWhere(id => !session.Unread.IsUnread(id));
        return _mutedAlerts.Select(id => session.Unread.KeyOf(id)).OfType<ConversationKey>().ToHashSet();
    }

    // 私聊看总开关；话题的 @我 / 提醒词、@全体、频道设置（未设置时用全局设置）
    // 是相互独立的触发条件，任一满足即提醒
    private (bool Popup, bool Sound) Decide(ConversationKey key, IReadOnlyList<string> flags)
    {
        var z = session.Settings.Zulip;
        if (key is not TopicKey t)
            return (z.EnableDesktopNotifications, z.EnableSounds);

        var channel = session.Channels.Get(t.StreamId);
        var popup = channel?.DesktopNotifications ?? z.EnableStreamDesktopNotifications;
        var sound = channel?.AudibleNotifications ?? z.EnableStreamAudibleNotifications;
        if (DecideMention(flags) is var (mentionPopup, mentionSound))
        {
            popup |= mentionPopup;
            sound |= mentionSound;
        }
        return (popup, sound);
    }

    // @我 / 提醒词，或 @全体（本地设置）时按总开关提醒；都不是时返回 null
    private (bool Popup, bool Sound)? DecideMention(IReadOnlyList<string> flags)
    {
        var z = session.Settings.Zulip;
        if (Contains(flags, "mentioned") || Contains(flags, "has_alert_word"))
            return (z.EnableDesktopNotifications, z.EnableSounds);
        if ((Contains(flags, "stream_wildcard_mentioned") || Contains(flags, "topic_wildcard_mentioned")
             || Contains(flags, "wildcard_mentioned"))
            && session.Settings.Local.WildcardMentionsNotify)
            return (z.EnableDesktopNotifications, z.EnableSounds);
        return null;
    }

    private NotificationRequest Build(ChatMessage m, bool popup, ConversationKey slot)
    {
        // 开发者模式模拟的消息 id 为负数，服务器上没有
        var messageId = m.Id > 0 ? m.Id : (long?)null;
        var title = session.Conversations.GetTitle(m.Key);
        string body;
        // “通知显示消息内容”只作用于私聊
        if (m.Key is DmKey && !session.Settings.Zulip.PmContentInDesktopNotifications)
        {
            body = "你收到了一条新消息";
        }
        else
        {
            var preview = ZulipHtml.ToPreview(m.ContentHtml);
            body = m.Key is DmKey { IsGroupDm: false } ? preview : $"{session.Contacts.DisplayName(m.SenderId)}：{preview}";
        }

        return m.Key switch
        {
            TopicKey t => new NotificationRequest(m.Key, title, body, null, title, session.Channels.Get(t.StreamId)?.Color, popup, slot,
                MessageId: messageId),
            DmKey { IsGroupDm: true } => new NotificationRequest(m.Key, title, body, null, "群", "#0052D9", popup, slot,
                MessageId: messageId),
            _ => new NotificationRequest(m.Key, title, body, session.Users.Get(m.SenderId)?.AvatarUrl ?? m.AvatarUrl, title, null,
                popup, slot, MessageId: messageId),
        };
    }

    private static bool Contains(IReadOnlyList<string> flags, string flag)
    {
        foreach (var f in flags)
        {
            if (f == flag)
                return true;
        }
        return false;
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "PlaySoundW")]
    private static extern bool PlaySound(string sound, IntPtr module, uint flags);
}
