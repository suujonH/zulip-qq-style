using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>会话的本地状态（与 conversation_state 表的一行对应）。</summary>
public sealed class ConversationEntry
{
    public ConversationKey Key { get; set; } = null!;

    /// <summary>最近一次看到的原始话题名。</summary>
    public string TitleCache { get; set; } = "";

    /// <summary>置顶时间（Unix 毫秒）；null 表示未置顶。</summary>
    public long? PinnedAt { get; set; }

    /// <summary>保存的免打扰级别；null 表示未设置（按允许提醒处理）。</summary>
    public DndLevel? StoredLevel { get; set; }

    public bool InList { get; set; }
    public string? Remark { get; set; }
    public long LastMessageId { get; set; }

    /// <summary>Unix 秒；0 表示没有消息。</summary>
    public long LastMessageTime { get; set; }

    public long LastSenderId { get; set; }
    public string LastPreview { get; set; } = "";

    public bool IsPinned => PinnedAt is not null;

    public static ConversationEntry FromRow(ConversationStateRow row) => new()
    {
        Key = row.Key,
        TitleCache = row.TitleCache,
        PinnedAt = row.PinnedAt,
        StoredLevel = row.DndLevel,
        InList = row.InList,
        Remark = row.Remark,
        LastMessageId = row.LastMessageId,
        LastMessageTime = row.LastMessageTime,
        LastSenderId = row.LastSenderId,
        LastPreview = row.LastPreview,
    };

    public ConversationStateRow ToRow() =>
        new(Key, TitleCache, PinnedAt, StoredLevel, InList, Remark, LastMessageId, LastMessageTime, LastSenderId, LastPreview);
}

/// <summary>
/// 会话列表：哪些会话在消息列表中、置顶、四级免打扰、备注、最后一条消息的预览。
/// 内存中的条目与 SQLite 保持一致（每次修改都写回）。只在 UI 线程上读写。
/// </summary>
public sealed class ConversationListStore(ZulipSession session, ConversationStateRepository repo)
{
    private const int BackfillIntervalMs = 300;
    private const int BackfillCount = 5;

    private readonly Dictionary<ConversationKey, ConversationEntry> _entries = [];

    // 缺少预览（或预览可能已过期）的会话，按顺序逐个向服务器查询最新一条消息
    private readonly Queue<ConversationKey> _backfill = [];
    private readonly HashSet<ConversationKey> _queued = [];
    private bool _backfillStarted;
    private bool _backfillRunning;

    /// <summary>某个会话的显示内容变化（预览、级别、备注等）。</summary>
    public event EventHandler<ConversationKey>? EntryChanged;

    /// <summary>列表成员或排序可能变化。</summary>
    public event EventHandler? ListChanged;

    public event EventHandler? Reset;

    /// <summary>话题被整体移动或改名，打开中的会话应换成新键并重新加载。</summary>
    public event EventHandler<(ConversationKey Old, ConversationKey New)>? ConversationRekeyed;

    public IReadOnlyCollection<ConversationEntry> Entries => _entries.Values;

    public ConversationEntry? Get(ConversationKey key) => _entries.GetValueOrDefault(key);

    public void LoadFromDb()
    {
        _entries.Clear();
        foreach (var row in repo.LoadAll())
            _entries[row.Key] = ConversationEntry.FromRow(row);
        Reset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 用预取的最近消息建立列表：每个会话取最新一条；新会话加入列表，离线期间有新消息的会话回到列表（屏蔽的除外）。
    /// </summary>
    public void Bootstrap(IEnumerable<MessageDto> recent)
    {
        // 被插件拦截的消息、系统通知和 Notification Bot 的提示不作为预览
        foreach (var group in recent.Where(IsPreviewable)
                     .GroupBy(m => ConversationKey.FromMessage(m, session.SelfId)))
        {
            var latest = group.MaxBy(m => m.Id)!;
            ApplyLatest(group.Key, latest.Id, latest.Timestamp, latest.SenderId, ZulipHtml.ToPreview(latest.Content), latest.Subject);
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 只知道最大消息 id 的会话（最近私聊、未读摘要）：比已保存的预览新时加入列表，并排队补取预览。
    /// <paramref name="isExactMax"/> 表示 maxId 是会话真实的最新消息 id（而不是最大的未读 id）。
    /// </summary>
    public void BootstrapKnown(ConversationKey key, long maxId, string title, bool isExactMax = false)
    {
        if (!_entries.TryGetValue(key, out var e))
        {
            e = new ConversationEntry { Key = key, TitleCache = title, InList = true };
            _entries[key] = e;
            repo.Upsert(e.ToRow());
            QueueBackfill(key);
        }
        else if (maxId > e.LastMessageId)
        {
            if (!e.InList && GetLevel(key) != DndLevel.Blocked)
            {
                e.InList = true;
                repo.Upsert(e.ToRow());
            }
            QueueBackfill(key);
        }
        else if (isExactMax && maxId < e.LastMessageId)
        {
            // 服务器报告的真实最大 id 变小：离线期间最后一条消息被删除，只重新补取预览，不算新动态
            QueueBackfill(key);
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>新消息（包括自己发送的）：更新预览；未屏蔽的会话回到列表。Notification Bot 的提示不更新。</summary>
    public void OnNewMessage(ChatMessage m)
    {
        if (IsBotNotice(m.SenderId, m.Key))
            return;
        ApplyLatest(m.Key, m.Id, m.Timestamp, m.SenderId, ZulipHtml.ToPreview(m.ContentHtml), m.Topic);
        EntryChanged?.Invoke(this, m.Key);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>消息被移动或话题改名（update_message 事件）。</summary>
    public void OnMessagesMoved(MoveInfo move)
    {
        var (oldKey, newKey) = (move.OldKey, move.NewKey);
        var newTitle = newKey.Topic;

        // 只改了大小写：同一个会话，只更新标题
        if (oldKey.Equals(newKey))
        {
            if (_entries.TryGetValue(oldKey, out var same))
            {
                same.TitleCache = newTitle;
                repo.SetTitle(oldKey, newTitle);
                EntryChanged?.Invoke(this, oldKey);
            }
            return;
        }

        // 联系人页的置顶独立保存，不依赖消息列表中是否有这个会话
        if (move.PropagateMode == "change_all")
            session.Contacts.RekeyTopic(oldKey, newKey);

        if (move.PropagateMode == "change_all" && _entries.Remove(oldKey, out var moved))
        {
            repo.Rename(oldKey, newKey, newTitle);
            if (_entries.TryGetValue(newKey, out var target))
            {
                // 目标会话已存在：保留目标的本地状态，预览以服务器为准重新获取
                target.TitleCache = newTitle;
                QueueBackfill(newKey);
            }
            else
            {
                moved.Key = newKey;
                moved.TitleCache = newTitle;
                _entries[newKey] = moved;
            }
            ConversationRekeyed?.Invoke(this, (oldKey, newKey));
        }
        else
        {
            // 部分移动：新会话以被移动的最新消息为预览；旧会话的最后一条被移走时重新获取预览
            var latestId = move.MessageIds.Count > 0 ? move.MessageIds.Max() : 0;
            if (session.Messages.Get(latestId) is { } latest)
                ApplyLatest(newKey, latest.Id, latest.Timestamp, latest.SenderId, ZulipHtml.ToPreview(latest.ContentHtml), newTitle);
            else
                BootstrapKnown(newKey, latestId, newTitle);

            if (_entries.TryGetValue(oldKey, out var old) && move.MessageIds.Contains(old.LastMessageId))
                QueueBackfill(oldKey);
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>消息被删除：最后一条被删除的会话重新获取预览。</summary>
    public void OnDeleted(IReadOnlyCollection<long> ids)
    {
        foreach (var e in _entries.Values.Where(e => ids.Contains(e.LastMessageId)).ToList())
            QueueBackfill(e.Key);
    }

    /// <summary>编辑了某会话的最后一条消息：更新预览。</summary>
    public void OnEdited(long messageId, string renderedHtml)
    {
        // 按消息 id 查找：预览可能来自补取，被编辑的消息不一定在消息缓存中
        if (_entries.Values.FirstOrDefault(x => x.LastMessageId == messageId) is not { } e)
            return;
        e.LastPreview = ZulipHtml.ToPreview(renderedHtml);
        repo.ReplacePreview(e.Key, e.LastMessageId, e.LastMessageTime, e.LastSenderId, e.LastPreview);
        EntryChanged?.Invoke(this, e.Key);
    }

    /// <summary>免打扰级别。只保存在本地，与 Zulip 的话题静音、频道静音无关；没有设置时为允许提醒。</summary>
    public DndLevel GetLevel(ConversationKey key) => key switch
    {
        TopicKey or DmKey => _entries.GetValueOrDefault(key)?.StoredLevel ?? DndLevel.Notify,
        _ => DndLevel.Notify,
    };

    /// <summary>开启“收起已解决话题”时，已解决的话题收进“已解决话题”（优先于群助手）。</summary>
    public bool IsCollapsedResolved(ConversationKey key) =>
        key is TopicKey t && session.Settings.Local.CollapseResolvedTopics && ViewModels.TopicActions.IsResolvedName(t.Topic);

    /// <summary>
    /// 用于提醒和未读总数的级别：收起的已解决话题与群助手一致，不提醒（屏蔽的保持屏蔽）；其余同 <see cref="GetLevel"/>。
    /// </summary>
    public DndLevel GetEffectiveLevel(ConversationKey key)
    {
        var level = GetLevel(key);
        return level != DndLevel.Blocked && IsCollapsedResolved(key) ? DndLevel.Assistant : level;
    }

    /// <summary>设置免打扰级别（只保存在本地）。屏蔽时从消息列表移除。</summary>
    public void SetLevel(ConversationKey key, DndLevel level)
    {
        var e = EnsureEntryInternal(key, "");
        e.StoredLevel = level;
        if (level == DndLevel.Blocked)
            e.InList = false;
        repo.Upsert(e.ToRow());
        EntryChanged?.Invoke(this, key);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(ConversationKey key, bool pinned)
    {
        var e = EnsureEntryInternal(key, "");
        e.PinnedAt = pinned ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null;
        repo.SetPinned(key, e.PinnedAt);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>从消息列表中移除（不影响其它状态）。再次打开时从底部开始。</summary>
    public void Remove(ConversationKey key)
    {
        session.Registry.ForgetScrollPosition(key);
        if (!_entries.TryGetValue(key, out var e) || !e.InList)
            return;
        e.InList = false;
        repo.SetInList(key, false);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 从列表中移除系统会话（机器人私聊、只有机器人公告的公告话题）：它们的内容在系统通知中显示。
    /// 不删除置顶、免打扰等本地设置。
    /// </summary>
    public void RemoveSystemEntries()
    {
        foreach (var e in _entries.Values.Where(e => e.InList && session.SystemNotices.IsSystemEntry(e)).ToList())
            Remove(e.Key);
    }

    /// <summary>从联系人、搜索等处主动打开会话时加入列表。</summary>
    public void EnsureInList(ConversationKey key)
    {
        var e = EnsureEntryInternal(key, "");
        if (e.InList)
            return;
        e.InList = true;
        repo.SetInList(key, true);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>行不存在时创建（不在列表中）；已存在时不做任何事。</summary>
    public void EnsureEntry(ConversationKey key, string title) => EnsureEntryInternal(key, title);

    /// <summary>设置话题或多人私聊的备注；空白视为清除。</summary>
    public void SetRemark(ConversationKey key, string? remark)
    {
        var normalized = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        var e = EnsureEntryInternal(key, "");
        if (e.Remark == normalized)
            return;
        e.Remark = normalized;
        repo.SetRemark(key, normalized);
        EntryChanged?.Invoke(this, key);
    }

    /// <summary>会话的显示标题（也用作文字头像）。已解决的话题去掉话题名开头的“✔ ”，标记见 <see cref="GetDisplayTitle"/>。</summary>
    public string GetTitle(ConversationKey key)
    {
        var e = _entries.GetValueOrDefault(key);
        switch (key)
        {
            case TopicKey t:
                if (!string.IsNullOrEmpty(e?.Remark))
                    return e.Remark;
                var name = ViewModels.TopicActions.StripResolved(GetTopicName(t));
                return name.Length == 0 ? session.Realm.EmptyTopicDisplayName : name;
            case DmKey { IsSelfDm: true }:
                return "我自己";
            case DmKey { OtherUserIds.Length: 1 } d:
                return session.Contacts.DisplayName(d.OtherUserIds[0]);
            case DmKey d:
                if (!string.IsNullOrEmpty(e?.Remark))
                    return e.Remark;
                var names = string.Join("、", d.OtherUserIds.Take(3).Select(session.Contacts.DisplayName));
                return d.OtherUserIds.Length > 3 ? $"{names}等{d.OtherUserIds.Length}人" : names;
            case ResolvedKey:
                return "已解决话题";
            default:
                return "群助手";
        }
    }

    /// <summary>
    /// 会话列表、聊天标题和独立窗口标题使用的标题：话题按设置中的模板拼接频道名与 <see cref="GetTitle"/>，
    /// 已解决的话题在最前面加上“(已解决)”；其它会话同 <see cref="GetTitle"/>。
    /// </summary>
    /// <param name="stripResolved">不加“(已解决)”（“已解决话题”中的会话都已解决，不再显示）。</param>
    public string GetDisplayTitle(ConversationKey key, bool stripResolved = false)
    {
        if (key is not TopicKey t)
            return GetTitle(key);
        var title = TopicTitleFormat.Format(session.Settings.Local.TopicTitleFormat, session.Channels.Get(t.StreamId)?.Name ?? "", GetTitle(key));
        return !stripResolved && ViewModels.TopicActions.IsResolvedName(GetTopicName(t))
            ? ViewModels.TopicActions.ResolvedLabel + title
            : title;
    }

    /// <summary>原始大小写的话题名，用于 narrow 和发送。</summary>
    public string GetTopicName(TopicKey key)
    {
        if (session.Messages.LatestInTimeline(key) is { } m)
            return m.Topic;
        if (session.Channels.TryGetTopics(key.StreamId)?.FirstOrDefault(t =>
                string.Equals(t.Name, key.Topic, StringComparison.OrdinalIgnoreCase)) is { } topic)
            return topic.Name;
        if (_entries.TryGetValue(key, out var e) && e.TitleCache.Length > 0)
            return e.TitleCache;
        return key.Topic;
    }

    /// <summary>开始按顺序为缺少预览的会话补取预览。之后加入队列的会话也由它处理。</summary>
    public void StartPreviewBackfill()
    {
        // 预览是 Notification Bot 的提示（如“标记为已解决”）：重新取前面的消息
        foreach (var e in _entries.Values.Where(e => e.LastMessageId > 0 && IsBotNotice(e.LastSenderId, e.Key)).ToList())
            QueueBackfill(e.Key);
        _backfillStarted = true;
        _ = RunBackfillAsync();
    }

    private void QueueBackfill(ConversationKey key)
    {
        if (_queued.Add(key))
            _backfill.Enqueue(key);
        if (_backfillStarted)
            _ = RunBackfillAsync();
    }

    private async Task RunBackfillAsync()
    {
        if (_backfillRunning)
            return;
        _backfillRunning = true;
        try
        {
            while (_backfill.Count > 0 && !session.IsDisposed)
            {
                var key = _backfill.Dequeue();
                _queued.Remove(key);
                try
                {
                    var narrow = Narrow.For(key, session.SelfId, key is TopicKey t ? GetTopicName(t) : null);
                    var before = _entries.TryGetValue(key, out var e0) ? e0.LastMessageId : 0;
                    var result = await session.Client.GetMessagesAsync(new MessageQuery(narrow, "newest", BackfillCount, 0), session.Token);
                    if (session.IsDisposed)
                        return;
                    // 跳过被插件拦截的消息、系统通知和 Notification Bot 的提示；最近几条都被跳过时保留原预览
                    var latest = result.Messages.LastOrDefault(IsPreviewable);
                    if (latest is null && result.Messages.Count > 0)
                        continue;
                    // 请求期间收到了更新的消息：响应已过时，保留事件写入的预览
                    if (_entries.TryGetValue(key, out var e1) && e1.LastMessageId != before && (latest?.Id ?? 0) < e1.LastMessageId)
                        continue;
                    ReplacePreview(key, latest);
                    await Task.Delay(BackfillIntervalMs, session.Token);
                }
                catch (OperationCanceledException) when (session.IsDisposed)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Warn($"补取会话预览失败：{key.ToStorageString()}", ex);
                }
            }
        }
        finally
        {
            _backfillRunning = false;
        }
    }

    private bool IsPreviewable(MessageDto m) =>
        !session.Messages.IsHidden(m) && !IsBotNotice(m.SenderId, ConversationKey.FromMessage(m, session.SelfId));

    // Notification Bot 在话题、私聊中发的提示（标记为已解决、话题被移动等）不作为预览；与它的私聊除外
    private bool IsBotNotice(long senderId, ConversationKey key) =>
        senderId == session.Users.NotificationBotId && !session.SystemNotices.IsBotDm(key);

    // 以服务器当前的最新消息为准覆盖预览；会话已没有消息时清空
    private void ReplacePreview(ConversationKey key, MessageDto? latest)
    {
        if (!_entries.TryGetValue(key, out var e))
            return;
        e.LastMessageId = latest?.Id ?? 0;
        e.LastMessageTime = latest?.Timestamp ?? 0;
        e.LastSenderId = latest?.SenderId ?? 0;
        e.LastPreview = latest is null ? "" : ZulipHtml.ToPreview(latest.Content);
        if (latest is { IsStream: true })
            e.TitleCache = latest.Subject;
        repo.Upsert(e.ToRow());
        EntryChanged?.Invoke(this, key);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    // 只有比已保存的更新时才更新预览；未屏蔽的会话回到列表
    private void ApplyLatest(ConversationKey key, long id, long time, long senderId, string preview, string title)
    {
        if (!_entries.TryGetValue(key, out var e))
        {
            e = new ConversationEntry { Key = key, TitleCache = title };
            _entries[key] = e;
        }
        else if (id < e.LastMessageId)
        {
            return;
        }
        else if (id == e.LastMessageId)
        {
            // 同一条消息（离线期间被编辑）：只刷新预览，不改变是否在列表中
            if (e.LastPreview != preview || (key is TopicKey && e.TitleCache != title))
            {
                e.LastPreview = preview;
                if (key is TopicKey)
                    e.TitleCache = title;
                repo.Upsert(e.ToRow());
            }
            return;
        }

        e.LastMessageId = id;
        e.LastMessageTime = time;
        e.LastSenderId = senderId;
        e.LastPreview = preview;
        if (key is TopicKey)
            e.TitleCache = title;
        if (GetLevel(key) != DndLevel.Blocked)
            e.InList = true;
        repo.Upsert(e.ToRow());
    }

    private ConversationEntry EnsureEntryInternal(ConversationKey key, string title)
    {
        if (_entries.TryGetValue(key, out var e))
            return e;
        e = new ConversationEntry { Key = key, TitleCache = title };
        _entries[key] = e;
        repo.Upsert(e.ToRow());
        return e;
    }
}
