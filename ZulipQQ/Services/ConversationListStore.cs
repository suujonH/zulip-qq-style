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

    /// <summary>保存的免打扰级别；null 表示未设置。实际级别见 <see cref="ConversationListStore.GetLevel"/>。</summary>
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
        foreach (var group in recent.GroupBy(m => ConversationKey.FromMessage(m, session.SelfId)))
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

    /// <summary>新消息（包括自己发送的）：更新预览；未屏蔽的会话回到列表；屏蔽的会话立即标记已读。</summary>
    public void OnNewMessage(ChatMessage m)
    {
        ApplyLatest(m.Key, m.Id, m.Timestamp, m.SenderId, ZulipHtml.ToPreview(m.ContentHtml), m.Topic);
        if (!m.IsRead && GetLevel(m.Key) == DndLevel.Blocked)
            _ = MarkReadQuietlyAsync([m.Id]);
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

    /// <summary>
    /// 实际的免打扰级别。话题按 Zulip 的静音状态校正已保存的级别（双向同步），校正结果写回 SQLite；私聊只看本地。
    /// </summary>
    public DndLevel GetLevel(ConversationKey key)
    {
        var e = _entries.GetValueOrDefault(key);
        switch (key)
        {
            case TopicKey t:
                if (session.Channels.Get(t.StreamId) is not { } channel)
                    return DndLevel.Notify;
                var muted = DndResolver.IsZulipMuted(channel.IsMuted, session.Channels.TopicPolicy(t.StreamId, t.Topic));
                var level = DndResolver.Resolve(e?.StoredLevel, muted);
                if (e?.StoredLevel is { } stored && stored != level)
                {
                    e.StoredLevel = level;
                    repo.SetDnd(key, level);
                }
                return level;
            case DmKey:
                return DndResolver.ResolveLocalOnly(e?.StoredLevel);
            default:
                return DndLevel.Notify;
        }
    }

    /// <summary>设置免打扰级别。话题先同步 Zulip 的静音状态，失败时抛出异常且不修改本地。</summary>
    public async Task SetLevelAsync(ConversationKey key, DndLevel level)
    {
        if (key is TopicKey t && session.Channels.Get(t.StreamId) is { } channel)
        {
            var current = session.Channels.TopicPolicy(t.StreamId, t.Topic);
            if (DndResolver.PolicyToApply(level, channel.IsMuted, current) is { } policy)
            {
                var topicName = GetTopicName(t);
                await session.Client.SetTopicVisibilityAsync(t.StreamId, topicName, policy, session.Token);
                if (session.IsDisposed)
                    return;
                // 立即在本地生效，否则 user_topic 事件到达前 GetLevel 会按旧状态把级别改回去
                session.Channels.OnUserTopic(new UserTopicDto { StreamId = t.StreamId, TopicName = topicName, VisibilityPolicy = policy });
            }
        }

        var e = EnsureEntryInternal(key, "");
        e.StoredLevel = level;
        if (level == DndLevel.Blocked)
            e.InList = false;
        repo.Upsert(e.ToRow());
        EntryChanged?.Invoke(this, key);
        ListChanged?.Invoke(this, EventArgs.Empty);

        if (level == DndLevel.Blocked)
            await MarkConversationReadQuietlyAsync(key);
    }

    /// <summary>频道静音或话题 visibility_policy 变化后重新计算级别。<paramref name="streamId"/> 为 null 时计算全部话题。</summary>
    public void RecomputeLevels(long? streamId)
    {
        foreach (var key in _entries.Keys.OfType<TopicKey>().Where(k => streamId is null || k.StreamId == streamId).ToList())
        {
            GetLevel(key);
            EntryChanged?.Invoke(this, key);
        }
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetPinned(ConversationKey key, bool pinned)
    {
        var e = EnsureEntryInternal(key, "");
        e.PinnedAt = pinned ? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() : null;
        repo.SetPinned(key, e.PinnedAt);
        ListChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>从消息列表中移除（不影响其它状态）。</summary>
    public void Remove(ConversationKey key)
    {
        if (!_entries.TryGetValue(key, out var e) || !e.InList)
            return;
        e.InList = false;
        repo.SetInList(key, false);
        ListChanged?.Invoke(this, EventArgs.Empty);
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

    /// <summary>会话的显示标题。</summary>
    public string GetTitle(ConversationKey key)
    {
        var e = _entries.GetValueOrDefault(key);
        switch (key)
        {
            case TopicKey t:
                if (!string.IsNullOrEmpty(e?.Remark))
                    return e.Remark;
                var name = GetTopicName(t);
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
            default:
                return "群助手";
        }
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

    /// <summary>离线期间屏蔽会话收到的消息：全部标记已读。</summary>
    public async Task MarkBlockedReadAsync()
    {
        foreach (var key in session.Unread.Keys.Where(k => GetLevel(k) == DndLevel.Blocked).ToList())
        {
            await MarkConversationReadQuietlyAsync(key);
            if (session.IsDisposed)
                return;
        }
    }

    /// <summary>开始按顺序为缺少预览的会话补取预览。之后加入队列的会话也由它处理。</summary>
    public void StartPreviewBackfill()
    {
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
                    var result = await session.Client.GetMessagesAsync(new MessageQuery(narrow, "newest", 1, 0), session.Token);
                    if (session.IsDisposed)
                        return;
                    var latest = result.Messages.LastOrDefault();
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

    private async Task MarkReadQuietlyAsync(IReadOnlyCollection<long> ids)
    {
        try
        {
            await session.Chat.MarkReadAsync(ids);
        }
        catch (Exception ex) when (!session.IsDisposed)
        {
            Log.Warn("屏蔽会话的消息标记已读失败", ex);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task MarkConversationReadQuietlyAsync(ConversationKey key)
    {
        try
        {
            await session.Chat.MarkConversationReadAsync(key);
        }
        catch (Exception ex) when (!session.IsDisposed)
        {
            Log.Warn($"会话标记已读失败：{key.ToStorageString()}", ex);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
