using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

public enum TimelineChangeKind
{
    /// <summary>窗口被整体替换（加载最新、加载某条消息附近），或本地回显被服务器消息替换。</summary>
    Replaced,

    /// <summary>在前面插入了更早的消息。</summary>
    Prepended,

    /// <summary>在末尾追加了消息。</summary>
    Appended,

    /// <summary>部分消息被删除或移出。</summary>
    Removed,

    /// <summary>窗口已被丢弃（有消息移入），打开中的会话应重新加载。</summary>
    Invalidated,
}

public sealed record TimelineChange(TimelineChangeKind Kind, int Count = 0);

/// <summary>某个会话已加载的一段连续消息（按 id 升序，本地回显的消息排在最后）。</summary>
public sealed class Timeline(ConversationKey key)
{
    public ConversationKey Key { get; } = key;

    public List<ChatMessage> Messages { get; } = [];

    /// <summary>窗口已包含最早的消息。</summary>
    public bool FoundOldest { get; internal set; }

    /// <summary>窗口已包含最新的消息；只有为 true 时新消息才会追加进来。</summary>
    public bool FoundNewest { get; internal set; }

    /// <summary>每次整体替换窗口时递增，用来丢弃过期的加载结果。</summary>
    internal int Version { get; set; }

    internal long LastAccess { get; set; }

    internal long FirstServerId => Messages.FirstOrDefault(m => m.Id > 0)?.Id ?? 0;

    internal long LastServerId => Messages.LastOrDefault(m => m.Id > 0)?.Id ?? 0;
}

/// <summary>
/// 消息缓存：全局的 id 索引，以及每个会话一个连续的消息窗口（最多保留 <see cref="MaxTimelines"/> 个，按最近使用淘汰）。
/// 只在 UI 线程上读写。
/// </summary>
public sealed class MessageStore(ZulipSession session)
{
    private const int MaxTimelines = 30;
    private const int MaxIndexedMessages = 5000;
    private const int PrunedIndexSize = 4000;

    private readonly Dictionary<long, ChatMessage> _byId = [];
    private readonly Dictionary<ConversationKey, Timeline> _timelines = [];

    // 实时收到时被插件拦截的消息：本次会话内加载历史时同样不显示
    private readonly HashSet<long> _hiddenByPlugin = [];

    // 本地回显，按发送时的 local_id 索引
    private readonly Dictionary<string, ChatMessage> _pending = [];

    private long _nextPendingId = -1;
    private long _accessCounter;

    /// <summary>正在打开的会话，不参与淘汰。</summary>
    public Func<ConversationKey, bool>? IsKeptOpen { get; set; }

    public event EventHandler<(ConversationKey Key, TimelineChange Change)>? TimelineChanged;

    public event EventHandler<long>? MessageUpdated;

    /// <summary>星标增减（含不在缓存中的消息），供收藏窗口使用。</summary>
    public event EventHandler<(IReadOnlyList<long> Ids, bool Added)>? StarredChanged;

    public event EventHandler? Reset;

    public void Clear()
    {
        _byId.Clear();
        _timelines.Clear();
        // 本地回显保留：发送中的由 10 秒兜底补上，发送失败的仍可重发
        Reset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>预取的最近消息放进全局索引，不建立窗口。</summary>
    public void Seed(IEnumerable<MessageDto> messages)
    {
        foreach (var dto in messages)
            Intern(dto);
    }

    public ChatMessage? Get(long id) => _byId.GetValueOrDefault(id);

    public Timeline? TryGet(ConversationKey key)
    {
        if (!_timelines.TryGetValue(key, out var timeline))
            return null;
        timeline.LastAccess = ++_accessCounter;
        return timeline;
    }

    /// <summary>某会话窗口中最新的一条服务器消息，用于取原始话题名；没有窗口时为 null。</summary>
    public ChatMessage? LatestInTimeline(ConversationKey key) =>
        _timelines.TryGetValue(key, out var timeline) ? timeline.Messages.LastOrDefault(m => m.Id > 0) : null;

    /// <summary>加载最新的消息，替换窗口。</summary>
    public async Task<Timeline> LoadNewestAsync(ConversationKey key, int count = 50)
    {
        var timeline = GetOrCreate(key);
        var version = ++timeline.Version;
        var result = await session.Client.GetMessagesAsync(
            new MessageQuery(NarrowFor(key), "newest", count, 0), session.Token);
        if (session.IsDisposed || timeline.Version != version)
            return timeline;

        ReplaceWindow(timeline, result.Messages, result.FoundOldest, foundNewest: true);
        Raise(key, new TimelineChange(TimelineChangeKind.Replaced));
        return timeline;
    }

    /// <summary>向前加载更早的消息，返回新增条数。</summary>
    public async Task<int> LoadOlderAsync(ConversationKey key, int count = 50)
    {
        if (TryGet(key) is not { } timeline || timeline.FoundOldest || timeline.FirstServerId == 0)
            return 0;

        var version = timeline.Version;
        var result = await session.Client.GetMessagesAsync(
            new MessageQuery(NarrowFor(key), Anchor(timeline.FirstServerId), count, 0, IncludeAnchor: false), session.Token);
        if (session.IsDisposed || timeline.Version != version)
            return 0;

        // 期间可能已有另一次加载插入了更早的消息，只取比当前第一条更早的
        var first = timeline.FirstServerId;
        var older = result.Messages.Where(m => m.Id < first).OrderBy(m => m.Id).Select(Intern).ToList();
        timeline.Messages.InsertRange(0, older);
        timeline.FoundOldest = result.FoundOldest;
        if (older.Count > 0)
            Raise(key, new TimelineChange(TimelineChangeKind.Prepended, older.Count));
        return older.Count;
    }

    /// <summary>向后加载更新的消息（窗口不在最新端时），返回新增条数。</summary>
    public async Task<int> LoadNewerAsync(ConversationKey key, int count = 50)
    {
        if (TryGet(key) is not { } timeline || timeline.FoundNewest || timeline.LastServerId == 0)
            return 0;

        var version = timeline.Version;
        var result = await session.Client.GetMessagesAsync(
            new MessageQuery(NarrowFor(key), Anchor(timeline.LastServerId), 0, count, IncludeAnchor: false), session.Token);
        if (session.IsDisposed || timeline.Version != version)
            return 0;

        var before = timeline.Messages.Count;
        foreach (var dto in result.Messages.Where(m => m.Id > timeline.LastServerId).OrderBy(m => m.Id))
            InsertSorted(timeline, Intern(dto));
        if (result.FoundNewest)
        {
            timeline.FoundNewest = true;
            MergeFromIndex(timeline);
        }
        var added = timeline.Messages.Count - before;
        if (added > 0)
            Raise(key, new TimelineChange(TimelineChangeKind.Appended, added));
        return added;
    }

    /// <summary>加载某条消息前后的消息，替换窗口。</summary>
    public async Task<Timeline> LoadAroundAsync(ConversationKey key, long anchorId, int before = 20, int after = 50)
    {
        var timeline = GetOrCreate(key);
        var version = ++timeline.Version;
        var result = await session.Client.GetMessagesAsync(
            new MessageQuery(NarrowFor(key), Anchor(anchorId), before, after), session.Token);
        if (session.IsDisposed || timeline.Version != version)
            return timeline;

        ReplaceWindow(timeline, result.Messages, result.FoundOldest, result.FoundNewest);
        Raise(key, new TimelineChange(TimelineChangeKind.Replaced));
        return timeline;
    }

    /// <summary>
    /// 新消息（message 事件，或发送后兜底查询到的消息）。
    /// <paramref name="localMessageId"/> 不为空且有对应的本地回显时，用这条消息替换回显。
    /// </summary>
    public void OnNewMessage(ChatMessage msg, string? localMessageId)
    {
        var existing = _byId.GetValueOrDefault(msg.Id);
        var target = existing ?? msg;
        if (existing is not null)
        {
            existing.ContentHtml = msg.ContentHtml;
            existing.Flags = msg.Flags;
        }
        else
        {
            _byId[msg.Id] = msg;
            if (_byId.Count > MaxIndexedMessages)
                PruneIndex();
        }

        if (localMessageId is not null && _pending.Remove(localMessageId, out var pending))
        {
            if (_timelines.TryGetValue(pending.Key, out var pendingTimeline))
            {
                pendingTimeline.Messages.Remove(pending);
                if (pendingTimeline.FoundNewest && !Contains(pendingTimeline, target.Id))
                    InsertSorted(pendingTimeline, target);
                Raise(pending.Key, new TimelineChange(TimelineChangeKind.Replaced));
            }
            return;
        }

        if (existing is not null)
        {
            MessageUpdated?.Invoke(this, existing.Id);
            return;
        }

        if (_timelines.TryGetValue(msg.Key, out var timeline) && timeline.FoundNewest && !Contains(timeline, msg.Id))
        {
            InsertSorted(timeline, msg);
            Raise(msg.Key, new TimelineChange(TimelineChangeKind.Appended, 1));
        }
    }

    /// <summary>update_message 事件：内容编辑，以及话题 / 频道的移动。</summary>
    public void OnUpdate(JsonElement ev)
    {
        if (ev.TryGetProperty("message_id", out var idElement) && ev.TryGetProperty("rendered_content", out var rendered)
            && _byId.TryGetValue(idElement.GetInt64(), out var edited))
        {
            edited.ContentHtml = rendered.GetString() ?? edited.ContentHtml;
            // 编辑可能增删提及，flags 以事件为准
            if (ev.TryGetProperty("flags", out var flags) && flags.ValueKind == JsonValueKind.Array)
                edited.Flags = [.. flags.EnumerateArray().Select(f => f.GetString() ?? "")];
            var renderingOnly = ev.TryGetProperty("rendering_only", out var ro) && ro.ValueKind == JsonValueKind.True;
            if (!renderingOnly && ev.TryGetProperty("edit_timestamp", out var editTs))
                edited.LastEditTimestamp = editTs.GetInt64();
            MessageUpdated?.Invoke(this, edited.Id);
        }

        if (MoveInfo.From(ev) is not { } move)
            return;

        foreach (var id in move.MessageIds)
        {
            if (!_byId.TryGetValue(id, out var m))
                continue;
            m.Topic = move.NewKey.Topic;
            m.StreamId = move.NewKey.StreamId;
            m.Key = move.NewKey;
            MessageUpdated?.Invoke(this, id);
        }

        // 只改了大小写：同一个会话，消息已就地更新
        if (move.OldKey.Equals(move.NewKey))
            return;

        if (_timelines.TryGetValue(move.OldKey, out var oldTimeline))
        {
            var ids = move.MessageIds.ToHashSet();
            if (oldTimeline.Messages.RemoveAll(m => ids.Contains(m.Id)) > 0)
                Raise(move.OldKey, new TimelineChange(TimelineChangeKind.Removed));
        }
        if (_timelines.Remove(move.NewKey))
            Raise(move.NewKey, new TimelineChange(TimelineChangeKind.Invalidated));
    }

    /// <summary>delete_message 事件。</summary>
    public void OnDeleted(IReadOnlyCollection<long> ids)
    {
        HashSet<ConversationKey> changed = [];
        foreach (var id in ids)
        {
            if (_byId.Remove(id, out var m))
                changed.Add(m.Key);
        }
        foreach (var timeline in _timelines.Values)
        {
            if (timeline.Messages.RemoveAll(m => ids.Contains(m.Id)) > 0)
                changed.Add(timeline.Key);
        }
        foreach (var key in changed)
            Raise(key, new TimelineChange(TimelineChangeKind.Removed));
    }

    /// <summary>reaction 事件。</summary>
    public void OnReaction(JsonElement ev)
    {
        if (!_byId.TryGetValue(ev.GetProperty("message_id").GetInt64(), out var m))
            return;

        var reaction = ZulipJson.Deserialize<ReactionDto>(ev);
        m.Reactions.RemoveAll(r => r.UserId == reaction.UserId && r.EmojiCode == reaction.EmojiCode
                                   && r.ReactionType == reaction.ReactionType);
        if (ev.GetProperty("op").GetString() == "add")
            m.Reactions.Add(reaction);
        MessageUpdated?.Invoke(this, m.Id);
    }

    /// <summary>update_message_flags 事件：更新缓存中消息的 flags（星标、已读等）。</summary>
    public void OnFlagEvent(JsonElement ev)
    {
        var flag = ev.GetProperty("flag").GetString() ?? "";
        var add = ev.GetProperty("op").GetString() == "add";
        IEnumerable<ChatMessage> targets;
        if (add && ev.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
            targets = _byId.Values.ToList();
        else
            targets = ev.GetProperty("messages").EnumerateArray()
                .Select(e => _byId.GetValueOrDefault(e.GetInt64())).OfType<ChatMessage>().ToList();

        foreach (var m in targets)
        {
            if (add ? m.Flags.Add(flag) : m.Flags.Remove(flag))
                MessageUpdated?.Invoke(this, m.Id);
        }

        if (flag == "starred" && ev.TryGetProperty("messages", out var ids))
            StarredChanged?.Invoke(this, (ids.EnumerateArray().Select(e => e.GetInt64()).ToList(), add));
    }

    /// <summary>发送前插入本地回显，内容暂时按纯文本显示。</summary>
    public ChatMessage AddPending(ConversationKey key, string content, string localId)
    {
        var self = session.Users.Get(session.SelfId);
        var msg = new ChatMessage
        {
            Id = _nextPendingId--,
            Key = key,
            SenderId = session.SelfId,
            SenderName = self?.FullName ?? "",
            SenderEmail = self?.Email ?? "",
            AvatarUrl = self?.AvatarUrl,
            ContentHtml = "<p>" + WebUtility.HtmlEncode(content).Replace("\n", "<br>", StringComparison.Ordinal) + "</p>",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Flags = ["read"],
            Topic = key is TopicKey t ? session.Conversations.GetTopicName(t) : "",
            StreamId = (key as TopicKey)?.StreamId,
            LocalId = localId,
            SendState = MessageSendState.Pending,
        };
        _pending[localId] = msg;

        if (_timelines.TryGetValue(key, out var timeline) && timeline.FoundNewest)
        {
            timeline.Messages.Add(msg);
            Raise(key, new TimelineChange(TimelineChangeKind.Appended, 1));
        }
        return msg;
    }

    /// <summary>
    /// 记录被插件拦截的消息。消息通常尚未进入索引；等待插件期间被加载进来时，标记后通知所在的会话重建。
    /// </summary>
    public void HideByPlugin(long id)
    {
        _hiddenByPlugin.Add(id);
        if (_byId.TryGetValue(id, out var msg) && !msg.IsHiddenByPlugin)
        {
            msg.IsHiddenByPlugin = true;
            Raise(msg.Key, new TimelineChange(TimelineChangeKind.Removed));
        }
    }

    /// <summary>本次会话中被插件拦截的消息（聊天记录窗口同样不显示）。</summary>
    public bool IsHiddenByPlugin(long id) => _hiddenByPlugin.Contains(id);

    /// <summary>本地回显是否仍在等待服务器的消息替换。</summary>
    public bool IsPending(string localId) => _pending.ContainsKey(localId);

    public void MarkFailed(string localId)
    {
        if (!_pending.TryGetValue(localId, out var msg))
            return;
        msg.SendState = MessageSendState.Failed;
        MessageUpdated?.Invoke(this, msg.Id);
    }

    /// <summary>移除发送失败的本地回显（重发前调用）。</summary>
    public void RemovePending(string localId)
    {
        if (!_pending.Remove(localId, out var msg))
            return;
        if (_timelines.TryGetValue(msg.Key, out var timeline) && timeline.Messages.Remove(msg))
            Raise(msg.Key, new TimelineChange(TimelineChangeKind.Removed));
    }

    private List<NarrowTerm> NarrowFor(ConversationKey key) =>
        Narrow.For(key, session.SelfId, key is TopicKey t ? session.Conversations.GetTopicName(t) : null);

    private static string Anchor(long id) => id.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>放进全局索引；已存在时用新数据更新并返回原对象，保证同一条消息只有一个对象。</summary>
    private ChatMessage Intern(MessageDto dto)
    {
        var key = ConversationKey.FromMessage(dto, session.SelfId);
        if (_byId.TryGetValue(dto.Id, out var existing))
        {
            existing.Key = key;
            existing.ContentHtml = dto.Content;
            existing.Flags = [.. dto.Flags];
            existing.Reactions = [.. dto.Reactions];
            existing.LastEditTimestamp = dto.LastEditTimestamp;
            existing.Topic = dto.Subject;
            existing.StreamId = dto.StreamId;
            existing.Submessages = dto.Submessages;
            return existing;
        }
        var msg = ChatMessage.FromDto(dto, key);
        msg.IsHiddenByPlugin = _hiddenByPlugin.Contains(msg.Id);
        _byId[msg.Id] = msg;
        return msg;
    }

    private void ReplaceWindow(Timeline timeline, IEnumerable<MessageDto> messages, bool foundOldest, bool foundNewest)
    {
        // 本地回显以 _pending 为准（窗口被淘汰或重建后也能恢复发送失败的消息）；负 id 越大越早发送
        var pending = _pending.Values.Where(m => m.Key.Equals(timeline.Key)).OrderByDescending(m => m.Id).ToList();
        timeline.Messages.Clear();
        timeline.Messages.AddRange(messages.OrderBy(m => m.Id).Select(Intern));
        timeline.FoundOldest = foundOldest;
        timeline.FoundNewest = foundNewest;
        if (!foundNewest)
            return;

        // 请求在途期间通过事件到达的消息不在响应中，从全局索引补上；本地回显总是排在最后
        MergeFromIndex(timeline);
        timeline.Messages.AddRange(pending);
    }

    private void MergeFromIndex(Timeline timeline)
    {
        var last = timeline.LastServerId;
        foreach (var m in _byId.Values.Where(m => m.Id > last && m.Key.Equals(timeline.Key)).OrderBy(m => m.Id).ToList())
            InsertSorted(timeline, m);
    }

    private static bool Contains(Timeline timeline, long id) => timeline.Messages.Exists(m => m.Id == id);

    // 按 id 升序插入，本地回显（负 id）始终在最后
    private static void InsertSorted(Timeline timeline, ChatMessage msg)
    {
        var list = timeline.Messages;
        var index = list.Count;
        while (index > 0 && (list[index - 1].Id < 0 || list[index - 1].Id > msg.Id))
            index--;
        list.Insert(index, msg);
    }

    private Timeline GetOrCreate(ConversationKey key)
    {
        if (!_timelines.TryGetValue(key, out var timeline))
        {
            timeline = new Timeline(key);
            _timelines[key] = timeline;
            Evict(timeline);
        }
        timeline.LastAccess = ++_accessCounter;
        return timeline;
    }

    private void Evict(Timeline keep)
    {
        while (_timelines.Count > MaxTimelines)
        {
            var victim = _timelines.Values
                .Where(t => t != keep && !(IsKeptOpen?.Invoke(t.Key) ?? false))
                .MinBy(t => t.LastAccess);
            if (victim is null)
                return;
            _timelines.Remove(victim.Key);
            foreach (var m in victim.Messages.Where(m => m.Id > 0))
                _byId.Remove(m.Id);
        }
    }

    // 不属于任何时间线的消息（预取与实时事件）只按 id 保留最新的一部分，避免长时间运行时无限增长
    private void PruneIndex()
    {
        HashSet<long> inTimeline = [.. _timelines.Values.SelectMany(t => t.Messages).Select(m => m.Id)];
        var loose = _byId.Keys.Where(id => id > 0 && !inTimeline.Contains(id)).Order().ToList();
        var removeCount = _byId.Count - PrunedIndexSize;
        foreach (var id in loose.Take(removeCount))
            _byId.Remove(id);
    }

    private void Raise(ConversationKey key, TimelineChange change) => TimelineChanged?.Invoke(this, (key, change));
}

/// <summary>update_message 事件中的话题 / 频道移动信息。</summary>
public sealed record MoveInfo(TopicKey OldKey, TopicKey NewKey, string PropagateMode, IReadOnlyList<long> MessageIds)
{
    /// <summary>事件中有 <c>subject</c> 或 <c>new_stream_id</c> 时返回移动信息，否则为 null。</summary>
    public static MoveInfo? From(JsonElement ev)
    {
        var hasTopic = ev.TryGetProperty("subject", out var subject);
        var hasStream = ev.TryGetProperty("new_stream_id", out var newStream);
        if ((!hasTopic && !hasStream) || !ev.TryGetProperty("stream_id", out var stream))
            return null;

        var origTopic = ev.TryGetProperty("orig_subject", out var orig) ? orig.GetString() ?? "" : "";
        var oldKey = new TopicKey(stream.GetInt64(), origTopic);
        var newKey = new TopicKey(hasStream ? newStream.GetInt64() : oldKey.StreamId,
            hasTopic ? subject.GetString() ?? "" : origTopic);
        var mode = ev.TryGetProperty("propagate_mode", out var p) ? p.GetString() ?? "change_one" : "change_one";
        var ids = ev.TryGetProperty("message_ids", out var idsElement)
            ? idsElement.EnumerateArray().Select(e => e.GetInt64()).ToList()
            : [];
        return new MoveInfo(oldKey, newKey, mode, ids);
    }
}
