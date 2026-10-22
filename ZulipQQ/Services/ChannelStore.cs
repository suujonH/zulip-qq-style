using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Services;

/// <summary>
/// 频道、话题的 visibility_policy，以及懒加载的话题列表和订阅者列表。只在 UI 线程上读写。
/// </summary>
public sealed class ChannelStore(ZulipSession session)
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private readonly Dictionary<long, Channel> _channels = [];

    // 话题不区分大小写，键用小写话题名
    private readonly Dictionary<(long StreamId, string Topic), int> _topicPolicy = [];
    private readonly Dictionary<long, List<TopicInfo>> _topics = [];
    private readonly Dictionary<long, List<long>> _subscribers = [];

    private static readonly TimeSpan AllTopicsInterval = TimeSpan.FromMilliseconds(200);
    private Task? _loadingAllTopics;

    public event EventHandler<long>? ChannelChanged;

    public event EventHandler<long>? TopicsChanged;

    public event EventHandler<long>? SubscribersChanged;

    public event EventHandler? Reset;

    public void Load(RegisterResult reg)
    {
        _channels.Clear();
        _topicPolicy.Clear();
        _topics.Clear();
        _subscribers.Clear();

        foreach (var s in reg.Streams)
            _channels[s.StreamId] = Channel.FromDto(s, subscribed: false);
        foreach (var s in reg.NeverSubscribed)
            _channels.TryAdd(s.StreamId, Channel.FromDto(s, subscribed: false));
        foreach (var s in reg.Unsubscribed)
            _channels[s.StreamId] = Channel.FromDto(s, subscribed: false);
        foreach (var s in reg.Subscriptions)
            _channels[s.StreamId] = Channel.FromDto(s, subscribed: true);

        foreach (var t in reg.UserTopics)
            SetPolicy(t.StreamId, t.TopicName, t.VisibilityPolicy);

        Reset?.Invoke(this, EventArgs.Empty);
    }

    public Channel? Get(long streamId) => _channels.GetValueOrDefault(streamId);

    public IEnumerable<Channel> All => _channels.Values;

    /// <summary>已订阅的频道：置顶的在前，再按名称（中文区域规则）排序。</summary>
    public IEnumerable<Channel> Subscribed() =>
        _channels.Values.Where(c => c.IsSubscribed)
            .OrderByDescending(c => c.PinToTop)
            .ThenBy(c => c.Name, NameComparer)
            .ThenBy(c => c.StreamId);

    /// <summary>话题的 visibility_policy（<see cref="TopicVisibility"/>）；没有设置时为 0。</summary>
    public int TopicPolicy(long streamId, string topic) =>
        _topicPolicy.GetValueOrDefault((streamId, topic.ToLowerInvariant()));

    /// <summary>已加载的话题列表；未加载时返回 null。</summary>
    public IReadOnlyList<TopicInfo>? TryGetTopics(long streamId) => _topics.GetValueOrDefault(streamId);

    /// <summary>频道的话题列表，按最新消息 id 降序。首次调用或 <paramref name="refresh"/> 时请求服务器。</summary>
    public async Task<IReadOnlyList<TopicInfo>> GetTopicsAsync(long streamId, bool refresh = false)
    {
        if (!refresh && _topics.TryGetValue(streamId, out var cached))
            return cached;

        var result = await session.Client.GetTopicsAsync(streamId, session.Token);
        if (session.IsDisposed)
            return [];
        var list = result.Select(t => new TopicInfo(t.Name, t.MaxId)).OrderByDescending(t => t.MaxId).ToList();
        _topics[streamId] = list;
        TopicsChanged?.Invoke(this, streamId);
        return list;
    }

    /// <summary>
    /// 依次加载所有已订阅频道中尚未加载的话题列表（全局搜索用），每个频道之间间隔 200ms，避免触发限流。
    /// 同时只运行一次；每加载完一个频道触发一次 <see cref="TopicsChanged"/>。失败的频道跳过。
    /// </summary>
    public Task EnsureAllTopicsLoadedAsync()
    {
        if (_loadingAllTopics is not { IsCompleted: false })
            _loadingAllTopics = LoadAllTopicsAsync();
        return _loadingAllTopics;
    }

    private async Task LoadAllTopicsAsync()
    {
        foreach (var streamId in Subscribed().Select(c => c.StreamId).ToList())
        {
            if (session.IsDisposed)
                return;
            if (_topics.ContainsKey(streamId))
                continue;
            try
            {
                await GetTopicsAsync(streamId);
                await Task.Delay(AllTopicsInterval, session.Token);
            }
            catch (Exception ex)
            {
                if (session.IsDisposed)
                    return;
                Log.Warn($"加载话题列表失败：{streamId}", ex);
            }
        }
    }

    /// <summary>频道的订阅者 id。首次调用时请求服务器，之后由 peer_add / peer_remove 事件维护。</summary>
    public async Task<IReadOnlyList<long>> GetSubscribersAsync(long streamId)
    {
        if (_subscribers.TryGetValue(streamId, out var cached))
            return cached;

        var result = await session.Client.GetSubscribersAsync(streamId, session.Token);
        if (session.IsDisposed)
            return [];
        var list = result.Distinct().ToList();
        _subscribers[streamId] = list;
        if (_channels.TryGetValue(streamId, out var channel))
            channel.SubscriberCount = list.Count;
        SubscribersChanged?.Invoke(this, streamId);
        return list;
    }

    /// <summary>新消息：已加载话题列表的频道，更新或插入该话题并保持按最新消息排序。</summary>
    public void OnNewMessage(ChatMessage m)
    {
        if (m.StreamId is not { } streamId || !_topics.TryGetValue(streamId, out var list))
            return;

        var index = list.FindIndex(t => string.Equals(t.Name, m.Topic, StringComparison.OrdinalIgnoreCase));
        if (index >= 0)
        {
            if (list[index].MaxId >= m.Id)
                return;
            list.RemoveAt(index);
        }
        list.Insert(0, new TopicInfo(m.Topic, m.Id));
        list.Sort((a, b) => b.MaxId.CompareTo(a.MaxId));
        TopicsChanged?.Invoke(this, streamId);
    }

    /// <summary>话题被移动或改名后，丢弃相关频道的话题缓存，下次使用时重新加载。</summary>
    public void InvalidateTopics(long streamId)
    {
        if (_topics.Remove(streamId))
            TopicsChanged?.Invoke(this, streamId);
    }

    /// <summary>
    /// subscription 事件。返回值为个人静音状态发生变化的频道 id（需要重新计算免打扰级别），否则为 null。
    /// </summary>
    public long? OnSubscriptionEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "add":
                foreach (var raw in ev.GetProperty("subscriptions").EnumerateArray())
                {
                    var dto = raw.Deserialize<SubscriptionDto>(ZulipJson.Options)!;
                    if (_channels.TryGetValue(dto.StreamId, out var existing))
                        existing.Apply(dto);
                    else
                        _channels[dto.StreamId] = existing = Channel.FromDto(dto, subscribed: true);
                    existing.IsSubscribed = true;
                    // 事件带有完整的订阅者列表；其中的 subscriber_count 可能是旧值（新建的频道为 0），以列表为准
                    if (raw.TryGetProperty("subscribers", out var subscribers) && subscribers.ValueKind == JsonValueKind.Array)
                    {
                        var list = subscribers.EnumerateArray().Select(u => u.GetInt64()).Distinct().ToList();
                        _subscribers[dto.StreamId] = list;
                        existing.SubscriberCount = list.Count;
                        SubscribersChanged?.Invoke(this, dto.StreamId);
                    }
                    ChannelChanged?.Invoke(this, dto.StreamId);
                }
                return null;

            case "remove":
                foreach (var s in ev.GetProperty("subscriptions").EnumerateArray())
                {
                    var id = s.GetProperty("stream_id").GetInt64();
                    if (_channels.TryGetValue(id, out var channel))
                    {
                        channel.IsSubscribed = false;
                        // 自己退订时没有 peer_remove 事件，订阅人数在这里减去自己
                        if (_subscribers.TryGetValue(id, out var list))
                        {
                            list.Remove(session.SelfId);
                            channel.SubscriberCount = list.Count;
                            SubscribersChanged?.Invoke(this, id);
                        }
                        else
                        {
                            channel.SubscriberCount = Math.Max(0, channel.SubscriberCount - 1);
                        }
                        ChannelChanged?.Invoke(this, id);
                    }
                }
                return null;

            case "update":
            {
                var id = ev.GetProperty("stream_id").GetInt64();
                if (!_channels.TryGetValue(id, out var channel))
                    return null;
                var property = ev.GetProperty("property").GetString() ?? "";
                var wasMuted = channel.IsMuted;
                channel.ApplySubscriptionProperty(property, ev.GetProperty("value"));
                ChannelChanged?.Invoke(this, id);
                return wasMuted != channel.IsMuted ? id : null;
            }

            case "peer_add":
            case "peer_remove":
                ApplyPeers(ev, ev.GetProperty("op").GetString() == "peer_add");
                return null;
        }
        return null;
    }

    /// <summary>stream 事件：创建、属性变化、删除。</summary>
    public void OnStreamEvent(JsonElement ev)
    {
        switch (ev.GetProperty("op").GetString())
        {
            case "create":
                foreach (var dto in ev.GetProperty("streams").Deserialize<List<StreamDto>>(ZulipJson.Options) ?? [])
                {
                    if (_channels.TryGetValue(dto.StreamId, out var existing))
                        existing.Apply(dto);
                    else
                        _channels[dto.StreamId] = Channel.FromDto(dto, subscribed: false);
                    ChannelChanged?.Invoke(this, dto.StreamId);
                }
                break;

            case "update":
            {
                var id = ev.GetProperty("stream_id").GetInt64();
                if (_channels.TryGetValue(id, out var channel))
                {
                    channel.ApplyStreamUpdate(ev);
                    ChannelChanged?.Invoke(this, id);
                }
                break;
            }

            case "delete":
                foreach (var id in DeletedStreamIds(ev))
                {
                    if (_channels.Remove(id))
                    {
                        _topics.Remove(id);
                        _subscribers.Remove(id);
                        ChannelChanged?.Invoke(this, id);
                    }
                }
                break;
        }
    }

    /// <summary>user_topic 事件。</summary>
    public void OnUserTopic(UserTopicDto t)
    {
        SetPolicy(t.StreamId, t.TopicName, t.VisibilityPolicy);
        ChannelChanged?.Invoke(this, t.StreamId);
    }

    private void SetPolicy(long streamId, string topic, int policy)
    {
        var key = (streamId, topic.ToLowerInvariant());
        if (policy == TopicVisibility.Inherit)
            _topicPolicy.Remove(key);
        else
            _topicPolicy[key] = policy;
    }

    private void ApplyPeers(JsonElement ev, bool add)
    {
        var userIds = ev.GetProperty("user_ids").EnumerateArray().Select(u => u.GetInt64()).ToList();
        foreach (var s in ev.GetProperty("stream_ids").EnumerateArray())
        {
            var id = s.GetInt64();
            if (!_channels.TryGetValue(id, out var channel))
                continue;

            if (_subscribers.TryGetValue(id, out var list))
            {
                if (add)
                    list.AddRange(userIds.Where(u => !list.Contains(u)));
                else
                    list.RemoveAll(userIds.Contains);
                channel.SubscriberCount = list.Count;
                SubscribersChanged?.Invoke(this, id);
            }
            else
            {
                channel.SubscriberCount = Math.Max(0, channel.SubscriberCount + (add ? userIds.Count : -userIds.Count));
            }
            ChannelChanged?.Invoke(this, id);
        }
    }

    // 新服务器用 stream_ids，旧服务器用 streams[].stream_id
    private static IEnumerable<long> DeletedStreamIds(JsonElement ev)
    {
        if (ev.TryGetProperty("stream_ids", out var ids))
            return ids.EnumerateArray().Select(i => i.GetInt64()).ToList();
        if (ev.TryGetProperty("streams", out var streams))
            return streams.EnumerateArray().Select(s => s.GetProperty("stream_id").GetInt64()).ToList();
        return [];
    }
}
