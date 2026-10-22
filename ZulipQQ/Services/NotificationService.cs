using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using ZulipQQ.Core;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>一次新消息提醒：是否弹出通知、是否播放提示音，以及通知显示的内容。</summary>
/// <param name="AvatarColor">文字头像的背景色（#RRGGBB）；为 null 时按文字取色板颜色。</param>
public sealed record NotificationRequest(
    ConversationKey Key,
    string Title,
    string Body,
    string? AvatarUrl,
    string AvatarText,
    string? AvatarColor,
    bool Popup);

/// <summary>
/// 新消息提醒：按免打扰级别和 Zulip 的通知设置决定是否弹窗、是否播放提示音。
/// 弹窗与任务栏闪烁由界面（App）订阅 <see cref="Requested"/> 处理。只在 UI 线程上使用。
/// </summary>
public sealed class NotificationService(ZulipSession session)
{
    private const uint SndAsync = 0x0001;
    private const uint SndNoDefault = 0x0002;
    private const uint SndAlias = 0x00010000;

    /// <summary>需要提醒（任务栏闪烁；<see cref="NotificationRequest.Popup"/> 为 true 时同时弹窗）。</summary>
    public event EventHandler<NotificationRequest>? Requested;

    /// <summary>
    /// 新消息：自己发的、已读的（如服务器代为标记已读的系统消息）、发送者被屏蔽、
    /// 会话不是“允许消息提醒”、正在前台查看该会话时不提醒。
    /// 其余情况总是请求闪烁任务栏；是否弹窗、是否有声音按 Zulip 的设置决定。
    /// </summary>
    public void OnNewMessage(ChatMessage m, IReadOnlyList<string> flags)
    {
        if (m.SenderId == session.SelfId
            || Contains(flags, "read")
            || session.Users.IsMuted(m.SenderId)
            || session.Conversations.GetLevel(m.Key) != DndLevel.Notify
            || session.Registry.IsActivelyViewing(m.Key))
            return;

        var (popup, sound) = Decide(m.Key, flags);
        // 提示音选择“无”时不播放任何提示音（与 Zulip 一致）
        if (sound && session.Settings.Zulip.NotificationSound != "none")
            PlaySound("Notification.IM", IntPtr.Zero, SndAlias | SndAsync | SndNoDefault);
        Requested?.Invoke(this, Build(m, popup));
    }

    // 私聊看总开关；话题的 @我 / 提醒词、@全体、关注的话题、频道设置（未设置时用全局设置）
    // 是相互独立的触发条件，任一满足即提醒（与 Zulip 网页端一致）
    private (bool Popup, bool Sound) Decide(ConversationKey key, IReadOnlyList<string> flags)
    {
        var z = session.Settings.Zulip;
        if (key is not TopicKey t)
            return (z.EnableDesktopNotifications, z.EnableSounds);

        var channel = session.Channels.Get(t.StreamId);
        var popup = channel?.DesktopNotifications ?? z.EnableStreamDesktopNotifications;
        var sound = channel?.AudibleNotifications ?? z.EnableStreamAudibleNotifications;
        if (Contains(flags, "mentioned") || Contains(flags, "has_alert_word"))
        {
            popup |= z.EnableDesktopNotifications;
            sound |= z.EnableSounds;
        }
        if ((Contains(flags, "stream_wildcard_mentioned") || Contains(flags, "topic_wildcard_mentioned")
             || Contains(flags, "wildcard_mentioned"))
            && (channel?.WildcardMentionsNotify ?? z.WildcardMentionsNotify))
        {
            popup |= z.EnableDesktopNotifications;
            sound |= z.EnableSounds;
        }
        if (session.Channels.TopicPolicy(t.StreamId, t.Topic) == 3)
        {
            popup |= z.EnableFollowedTopicDesktopNotifications;
            sound |= z.EnableFollowedTopicAudibleNotifications;
        }
        return (popup, sound);
    }

    private NotificationRequest Build(ChatMessage m, bool popup)
    {
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
            TopicKey t => new NotificationRequest(m.Key, title, body, null, title, session.Channels.Get(t.StreamId)?.Color, popup),
            DmKey { IsGroupDm: true } => new NotificationRequest(m.Key, title, body, null, "群", "#0052D9", popup),
            _ => new NotificationRequest(m.Key, title, body, session.Users.Get(m.SenderId)?.AvatarUrl ?? m.AvatarUrl, title, null, popup),
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
