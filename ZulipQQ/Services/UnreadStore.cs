using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>打开会话时的未读快照：全部未读 id（升序）与其中提及自己的消息。</summary>
public sealed record UnreadSnapshot(IReadOnlyList<long> Ids, IReadOnlyList<(long Id, MentionKind Kind)> Mentions);

/// <summary>
/// 各会话的未读消息，以及其中的提及、文件。register 时读取摘要，再用未读消息详情补全提及类型与文件；
/// 之后由 message、update_message_flags、delete_message、update_message 事件维护。只在 UI 线程上读写。
/// </summary>
public sealed class UnreadStore(ZulipSession session)
{
    private readonly Dictionary<ConversationKey, SortedSet<long>> _ids = [];
    private readonly Dictionary<long, ConversationKey> _keyOf = [];
    private readonly Dictionary<long, MentionKind> _mention = [];
    private readonly HashSet<long> _hasFile = [];
    // 正在向服务器查询详情的“标为未读”消息；查询期间又被标为已读时从这里移除，查询结果随之作废
    private readonly HashSet<long> _pendingUnread = [];

    public event EventHandler<ConversationKey>? ConversationChanged;

    public event EventHandler? Reset;

    public void Load(UnreadMsgsDto u)
    {
        _ids.Clear();
        _keyOf.Clear();
        _mention.Clear();
        _hasFile.Clear();
        _pendingUnread.Clear();

        var self = session.SelfId;
        foreach (var s in u.Streams)
            AddAll(new TopicKey(s.StreamId, s.Topic), s.UnreadMessageIds);
        foreach (var p in u.Pms)
            AddAll(p.OtherUserId == self ? new DmKey([]) : DmKey.Create([p.OtherUserId], self), p.UnreadMessageIds);
        foreach (var h in u.Huddles)
        {
            var ids = h.UserIdsString.Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(s => long.Parse(s, CultureInfo.InvariantCulture));
            AddAll(DmKey.Create(ids, self), h.UnreadMessageIds);
        }
        // 摘要无法区分 @我 与 @全体，先按 @我 处理，由 ApplyDetails 修正
        foreach (var id in u.Mentions)
        {
            if (_keyOf.ContainsKey(id))
                _mention[id] = MentionKind.Personal;
        }

        Reset?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>用未读消息的详情修正提及类型，并记录含文件的消息。</summary>
    public void ApplyDetails(IEnumerable<MessageDto> messages)
    {
        foreach (var m in messages)
        {
            if (!_keyOf.TryGetValue(m.Id, out var key))
                continue;
            SetDetails(m.Id, m.Flags, m.Content);
            ConversationChanged?.Invoke(this, key);
        }
    }

    /// <summary>新消息：flags 不含 read 时加入未读。</summary>
    public void OnNewMessage(ChatMessage m, IReadOnlyCollection<string> flags)
    {
        if (flags.Contains("read") || _keyOf.ContainsKey(m.Id))
            return;
        Add(m.Key, m.Id);
        SetDetails(m.Id, flags, m.ContentHtml);
        ConversationChanged?.Invoke(this, m.Key);
    }

    /// <summary>update_message_flags 事件中的 read 标志。</summary>
    public void OnReadFlagEvent(JsonElement ev)
    {
        var op = ev.GetProperty("op").GetString();
        var ids = ev.GetProperty("messages").EnumerateArray().Select(e => e.GetInt64()).ToList();
        if (op == "add")
        {
            if (ev.TryGetProperty("all", out var all) && all.ValueKind == JsonValueKind.True)
            {
                var keys = _ids.Keys.ToList();
                _ids.Clear();
                _keyOf.Clear();
                _mention.Clear();
                _hasFile.Clear();
                _pendingUnread.Clear();
                foreach (var key in keys)
                    ConversationChanged?.Invoke(this, key);
                return;
            }
            MarkLocallyRead(ids);
            return;
        }

        if (op != "remove")
            return;

        // 标为未读：从 message_details 得到会话；缺少详情的消息向服务器查询
        ev.TryGetProperty("message_details", out var details);
        List<long> missing = [];
        HashSet<ConversationKey> changed = [];
        foreach (var id in ids)
        {
            if (_keyOf.ContainsKey(id))
                continue;
            if (details.ValueKind != JsonValueKind.Object
                || !details.TryGetProperty(id.ToString(CultureInfo.InvariantCulture), out var d)
                || KeyFromDetails(d) is not { } key)
            {
                missing.Add(id);
                continue;
            }
            // 详情中的 mentioned 不区分 @我 与 @全体：优先用缓存的消息计算；
            // 没有缓存且带提及时向服务器查询（不带提及的直接加入，避免批量标未读时逐条请求）
            if (session.Messages.Get(id) is { } cached)
            {
                Add(key, id);
                SetDetails(id, cached.Flags, cached.ContentHtml);
                changed.Add(key);
            }
            else if (d.TryGetProperty("mentioned", out var mentioned) && mentioned.ValueKind == JsonValueKind.True)
            {
                missing.Add(id);
            }
            else
            {
                Add(key, id);
                changed.Add(key);
            }
        }
        foreach (var key in changed)
            ConversationChanged?.Invoke(this, key);
        if (missing.Count > 0)
        {
            _pendingUnread.UnionWith(missing);
            _ = FetchMissingAsync(missing);
        }
    }

    /// <summary>消息被编辑：未读消息按新的 flags 与内容重新计算提及和文件。</summary>
    public void OnEdited(long id, IReadOnlyCollection<string> flags, string html)
    {
        if (!_keyOf.TryGetValue(id, out var key))
            return;
        SetDetails(id, flags, html);
        ConversationChanged?.Invoke(this, key);
    }

    public void OnDeleted(IEnumerable<long> ids) => MarkLocallyRead(ids);

    /// <summary>消息被移动到另一个会话。</summary>
    public void OnMoved(IEnumerable<long> ids, ConversationKey newKey)
    {
        HashSet<ConversationKey> changed = [];
        foreach (var id in ids)
        {
            if (!_keyOf.TryGetValue(id, out var oldKey))
                continue;
            if (_ids.TryGetValue(oldKey, out var set))
            {
                set.Remove(id);
                if (set.Count == 0)
                    _ids.Remove(oldKey);
            }
            changed.Add(oldKey);
            Add(newKey, id);
            changed.Add(newKey);
        }
        foreach (var key in changed)
            ConversationChanged?.Invoke(this, key);
    }

    public int Count(ConversationKey key) => _ids.TryGetValue(key, out var set) ? set.Count : 0;

    public IEnumerable<ConversationKey> Keys => _ids.Keys;

    public bool IsUnread(long id) => _keyOf.ContainsKey(id);

    /// <summary>某会话中最大的未读 id；没有未读时为 0。</summary>
    public long MaxId(ConversationKey key) => _ids.TryGetValue(key, out var set) ? set.Max : 0;

    /// <summary>会话预览前的提示，优先级 @我 &gt; @全体 &gt; 新文件。</summary>
    public ConversationNotice GetNotice(ConversationKey key)
    {
        if (!_ids.TryGetValue(key, out var set))
            return ConversationNotice.None;
        var notice = ConversationNotice.None;
        foreach (var id in set)
        {
            switch (_mention.GetValueOrDefault(id))
            {
                case MentionKind.Personal:
                    return ConversationNotice.AtMe;
                case MentionKind.Wildcard:
                    notice = ConversationNotice.AtAll;
                    break;
                default:
                    if (notice == ConversationNotice.None && _hasFile.Contains(id))
                        notice = ConversationNotice.NewFile;
                    break;
            }
        }
        return notice;
    }

    public UnreadSnapshot Snapshot(ConversationKey key)
    {
        if (!_ids.TryGetValue(key, out var set))
            return new UnreadSnapshot([], []);
        var ids = set.ToList();
        var mentions = ids.Where(id => _mention.GetValueOrDefault(id) != MentionKind.None)
            .Select(id => (id, _mention[id]))
            .ToList();
        return new UnreadSnapshot(ids, mentions);
    }

    public void MarkLocallyRead(ConversationKey key)
    {
        if (!_ids.Remove(key, out var set))
            return;
        foreach (var id in set)
            Forget(id);
        ConversationChanged?.Invoke(this, key);
    }

    public void MarkLocallyRead(IEnumerable<long> ids)
    {
        HashSet<ConversationKey> changed = [];
        foreach (var id in ids)
        {
            _pendingUnread.Remove(id);
            if (!_keyOf.TryGetValue(id, out var key))
                continue;
            Forget(id);
            if (_ids.TryGetValue(key, out var set))
            {
                set.Remove(id);
                if (set.Count == 0)
                    _ids.Remove(key);
            }
            changed.Add(key);
        }
        foreach (var key in changed)
            ConversationChanged?.Invoke(this, key);
    }

    /// <summary>导航栏角标的总数：只统计级别为“允许消息提醒”的会话。</summary>
    public int TotalForBadge(Func<ConversationKey, DndLevel> level) =>
        _ids.Where(p => level(p.Key) == DndLevel.Notify).Sum(p => p.Value.Count);

    private async Task FetchMissingAsync(List<long> ids)
    {
        foreach (var id in ids)
        {
            try
            {
                var m = await session.Client.GetMessageAsync(id, ct: session.Token);
                if (session.IsDisposed)
                    return;
                // 查询期间可能又被标为已读，此时消息的 flags 中含 read
                if (!_pendingUnread.Remove(id) || _keyOf.ContainsKey(id) || m.Flags.Contains("read"))
                    continue;
                var key = ConversationKey.FromMessage(m, session.SelfId);
                Add(key, id);
                SetDetails(id, m.Flags, m.Content);
                ConversationChanged?.Invoke(this, key);
            }
            catch (OperationCanceledException) when (session.IsDisposed)
            {
                return;
            }
            catch (Exception ex)
            {
                if (session.IsDisposed)
                    return;
                Log.Warn($"查询标为未读的消息 {id} 失败", ex);
            }
        }
    }

    private ConversationKey? KeyFromDetails(JsonElement d)
    {
        switch (d.GetProperty("type").GetString())
        {
            case "stream":
                return new TopicKey(d.GetProperty("stream_id").GetInt64(), d.GetProperty("topic").GetString() ?? "");
            case "private":
                var ids = d.TryGetProperty("user_ids", out var u) ? u.EnumerateArray().Select(e => e.GetInt64()) : [];
                return DmKey.Create(ids, session.SelfId);
            default:
                return null;
        }
    }

    private void SetDetails(long id, IReadOnlyCollection<string> flags, string html)
    {
        if (flags.Contains("mentioned"))
            _mention[id] = MentionKind.Personal;
        else if (flags.Contains("stream_wildcard_mentioned") || flags.Contains("topic_wildcard_mentioned")
                 || flags.Contains("wildcard_mentioned"))
            _mention[id] = MentionKind.Wildcard;
        else
            _mention.Remove(id);

        if (ZulipHtml.HasFileAttachment(html))
            _hasFile.Add(id);
        else
            _hasFile.Remove(id);
    }

    private void AddAll(ConversationKey key, IEnumerable<long> ids)
    {
        foreach (var id in ids)
            Add(key, id);
    }

    private void Add(ConversationKey key, long id)
    {
        if (!_ids.TryGetValue(key, out var set))
            _ids[key] = set = [];
        set.Add(id);
        _keyOf[id] = key;
    }

    private void Forget(long id)
    {
        _keyOf.Remove(id);
        _mention.Remove(id);
        _hasFile.Remove(id);
    }
}
