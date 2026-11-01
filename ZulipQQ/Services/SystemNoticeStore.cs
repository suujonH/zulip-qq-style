using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>系统通知中的一条（公告或机器人私信），也用于已到时间的消息提醒。</summary>
public sealed record SystemNoticeEntry(long Id, SystemNoticeCategory Category, string Html, long Timestamp,
    long? StreamId, string Topic, long[] MentionedUserIds);

/// <summary>
/// Notification Bot 的消息：区分灰色提示条、系统通知与消息提醒，保存系统通知和已到时间的提醒及其已读状态。
/// 这些消息不计入消息列表的未读。只在 UI 线程上读写。
/// </summary>
public sealed partial class SystemNoticeStore(ZulipSession session)
{
    private const int LoadCount = 300;

    private readonly Dictionary<long, SystemNoticeEntry> _notices = [];
    private readonly Dictionary<long, SystemNoticeEntry> _reminders = [];
    private readonly HashSet<long> _unread = [];
    private int _version;

    /// <summary>系统通知、提醒或已读状态有变化（或重新载入）。</summary>
    public event EventHandler? Changed;

    /// <summary>首次载入中。</summary>
    public bool IsLoading { get; private set; }

    /// <summary>点击“清空”时最新一条系统通知的 id；不大于它的不再显示。</summary>
    private long ClearedBeforeId => session.Settings.Local.SystemNoticesClearedBefore;

    /// <summary>系统通知，新的在前。</summary>
    public IReadOnlyList<SystemNoticeEntry> Notices =>
        _notices.Values.Where(e => e.Id > ClearedBeforeId).OrderByDescending(e => e.Id).ToList();

    /// <summary>已到时间的消息提醒，新的在前。</summary>
    public IReadOnlyList<SystemNoticeEntry> DeliveredReminders => _reminders.Values.OrderByDescending(e => e.Id).ToList();

    public int UnreadNoticeCount => _unread.Count(id => id > ClearedBeforeId && _notices.ContainsKey(id));

    public int UnreadReminderCount => _unread.Count(_reminders.ContainsKey);

    public bool IsUnread(long id) => _unread.Contains(id);

    public SystemNoticeKind Classify(MessageDto dto) =>
        SystemNoticeRules.Classify(session.Users.NotificationBotId, session.Realm.AnnouncementChannelIds,
            dto.SenderId, dto.IsStream ? dto.StreamId : null, dto.Subject, dto.Content);

    public SystemNoticeKind Classify(ChatMessage m) =>
        SystemNoticeRules.Classify(session.Users.NotificationBotId, session.Realm.AnnouncementChannelIds,
            m.SenderId, m.Key is TopicKey ? m.StreamId : null, m.Topic, m.ContentHtml);

    /// <summary>与 Notification Bot 的私聊：内容只在系统通知和消息提醒中显示。</summary>
    public bool IsBotDm(ConversationKey key) => session.Users.NotificationBotDmKey is { } k && k.Equals(key);

    /// <summary>不应出现在会话列表中的会话：机器人私聊，或最新一条是机器人公告的公告话题。</summary>
    public bool IsSystemEntry(ConversationEntry e) =>
        IsBotDm(e.Key)
        || e.Key is TopicKey t && session.Realm.AnnouncementChannelIds.Contains(t.StreamId)
                               && SystemNoticeRules.IsAnnouncementTopic(t.Topic)
                               && e.LastSenderId == session.Users.NotificationBotId;

    /// <summary>重新载入 Notification Bot 最近发送的消息。</summary>
    public async Task LoadAsync()
    {
        var version = ++_version;
        _notices.Clear();
        _reminders.Clear();
        _unread.Clear();
        IsLoading = true;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (session.Users.NotificationBotId is { } botId)
            {
                var result = await session.Client.GetMessagesAsync(
                    new MessageQuery([Narrow.Sender(botId)], "newest", LoadCount, 0), session.Token);
                if (session.IsDisposed || version != _version)
                    return;
                foreach (var dto in result.Messages)
                    Add(dto.Id, Classify(dto), dto.IsStream ? dto.StreamId : null, dto.Subject, dto.Content,
                        dto.Timestamp, dto.Flags.Contains("read"));
            }
        }
        catch (Exception ex) when (!session.IsDisposed)
        {
            Log.Warn("载入系统通知失败", ex);
        }
        if (session.IsDisposed || version != _version)
            return;
        IsLoading = false;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新消息（已由 <see cref="Classify(ChatMessage)"/> 分类）。</summary>
    public void OnNewMessage(ChatMessage m)
    {
        if (Add(m.Id, m.Notice, m.Key is TopicKey ? m.StreamId : null, m.Topic, m.ContentHtml, m.Timestamp, m.IsRead))
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>update_message_flags 事件：只关心已读。</summary>
    public void OnFlagEvent(JsonElement ev)
    {
        if (ev.GetProperty("flag").GetString() != "read")
            return;
        var add = ev.GetProperty("op").GetString() == "add";
        var changed = false;
        if (add && ev.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
        {
            changed = _unread.Count > 0;
            _unread.Clear();
        }
        else
        {
            foreach (var id in ev.GetProperty("messages").EnumerateArray().Select(e => e.GetInt64()))
            {
                if (add)
                    changed |= _unread.Remove(id);
                else if (_notices.ContainsKey(id) || _reminders.ContainsKey(id))
                    changed |= _unread.Add(id);
            }
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    public void OnDeleted(IReadOnlyCollection<long> ids)
    {
        var changed = false;
        foreach (var id in ids)
        {
            changed |= _notices.Remove(id);
            changed |= _reminders.Remove(id);
            _unread.Remove(id);
        }
        if (changed)
            Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>打开系统通知时调用：本地立即清除红点，再提交服务器。</summary>
    public Task MarkNoticesReadAsync() => MarkReadAsync(_notices.Keys);

    /// <summary>查看已到时间的提醒时调用。</summary>
    public Task MarkRemindersReadAsync() => MarkReadAsync(_reminders.Keys);

    /// <summary>清空系统通知：只在本地不再显示现有的通知，不删除服务器上的消息。</summary>
    public async Task ClearNoticesAsync()
    {
        if (_notices.Count == 0)
            return;
        var max = _notices.Keys.Max();
        await MarkNoticesReadAsync();
        if (session.IsDisposed)
            return;
        session.Settings.Local.SystemNoticesClearedBefore = max;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task MarkReadAsync(IEnumerable<long> source)
    {
        var ids = source.Where(_unread.Contains).ToList();
        if (ids.Count == 0)
            return;
        foreach (var id in ids)
            _unread.Remove(id);
        Changed?.Invoke(this, EventArgs.Empty);
        // 失败时不恢复红点，下次同步以服务器为准
        try
        {
            await session.Chat.MarkReadAsync(ids);
        }
        catch (Exception ex) when (!session.IsDisposed)
        {
            Log.Warn("系统通知标记已读失败", ex);
        }
    }

    private bool Add(long id, SystemNoticeKind kind, long? streamId, string topic, string html, long timestamp, bool isRead)
    {
        var target = kind switch
        {
            SystemNoticeKind.Notice => _notices,
            SystemNoticeKind.Reminder => _reminders,
            _ => null,
        };
        if (target is null)
            return false;
        target[id] = new SystemNoticeEntry(id, SystemNoticeRules.CategoryOf(streamId, topic), html, timestamp,
            streamId, topic, MentionedIds(html));
        if (isRead)
            _unread.Remove(id);
        else
            _unread.Add(id);
        return true;
    }

    // 卡片头像：通知中提到的第一个用户（例如新成员、创建频道的人）
    private static long[] MentionedIds(string html) =>
        UserIdPattern().Matches(html).Select(m => long.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .Distinct().ToArray();

    [GeneratedRegex("data-user-id=\"(\\d+)\"")]
    private static partial Regex UserIdPattern();
}
