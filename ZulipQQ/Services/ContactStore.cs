using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ZulipQQ.Core;
using ZulipQQ.Data;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>
/// 好友分组、好友备注与联系人页的置顶（只保存在本地）。联系人页、好友管理器都从这里读取，所有修改也都经过这里，
/// 修改后触发 <see cref="Changed"/>，各界面据此刷新。“我的好友”是虚拟分组，显示全部用户，不在 <see cref="Groups"/> 中。
/// </summary>
public sealed class ContactStore(ContactRepository repo, UserStore users)
{
    private readonly List<FriendGroup> _groups = [];
    private readonly Dictionary<long, long> _membership = [];
    private readonly Dictionary<long, string> _remarks = [];

    // 联系人页的置顶：好友 u:{用户id}，话题为会话键。与消息列表的置顶（ConversationListStore）相互独立
    private readonly Dictionary<string, long> _pins = [];

    public event EventHandler? Changed;

    /// <summary>按创建顺序排列的自定义分组。</summary>
    public IReadOnlyList<FriendGroup> Groups => _groups;

    public void Load()
    {
        _groups.Clear();
        _groups.AddRange(repo.LoadGroups());
        _membership.Clear();
        foreach (var (user, group) in repo.LoadMembership())
            _membership[user] = group;
        _remarks.Clear();
        foreach (var (user, remark) in repo.LoadRemarks())
            _remarks[user] = remark;
        _pins.Clear();
        foreach (var (key, at) in repo.LoadPins())
            _pins[key] = at;
    }

    /// <summary>当前时间（Unix 毫秒），测试中可替换。</summary>
    public Func<long> Now { get; init; } = () => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool IsFriendPinned(long userId) => _pins.ContainsKey(FriendPinKey(userId));

    public bool IsTopicPinned(TopicKey key) => _pins.ContainsKey(key.ToStorageString());

    /// <summary>在联系人页置顶或取消置顶好友，不影响消息列表中私聊的置顶。</summary>
    public void SetFriendPinned(long userId, bool pinned) => SetPin(FriendPinKey(userId), pinned);

    /// <summary>在联系人页置顶或取消置顶话题（显示在“置顶群聊”中），不影响消息列表中会话的置顶。</summary>
    public void SetTopicPinned(TopicKey key, bool pinned) => SetPin(key.ToStorageString(), pinned);

    /// <summary>置顶的好友，最后置顶的在前。</summary>
    public IReadOnlyList<long> PinnedFriends() =>
        [.. _pins.Where(p => p.Key.StartsWith(FriendPrefix, StringComparison.Ordinal))
            .OrderByDescending(p => p.Value)
            .Select(p => long.Parse(p.Key.AsSpan(FriendPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture))];

    /// <summary>置顶的话题，最后置顶的在前。话题名是小写的（来自会话键），只能用于比较和查找。</summary>
    public IReadOnlyList<TopicKey> PinnedTopics() =>
        [.. _pins.Where(p => p.Key.StartsWith("t:", StringComparison.Ordinal))
            .OrderByDescending(p => p.Value)
            .Select(p => ConversationKey.Parse(p.Key))
            .OfType<TopicKey>()];

    /// <summary>话题被整体移动或改名：置顶跟着移到新话题（新话题已置顶时保留新话题的）。</summary>
    public void RekeyTopic(TopicKey oldKey, TopicKey newKey)
    {
        var (oldS, newS) = (oldKey.ToStorageString(), newKey.ToStorageString());
        if (oldS == newS || !_pins.Remove(oldS, out var at))
            return;
        repo.SetPin(oldS, null);
        if (_pins.TryAdd(newS, at))
            repo.SetPin(newS, at);
        RaiseChanged();
    }

    private const string FriendPrefix = "u:";

    private static string FriendPinKey(long userId) => FriendPrefix + userId.ToString(CultureInfo.InvariantCulture);

    // 置顶时间严格递增：同一毫秒内连续置顶时顺序也确定（后置顶的在前）
    private void SetPin(string key, bool pinned)
    {
        if (pinned == _pins.ContainsKey(key))
            return;
        if (pinned)
        {
            var at = Math.Max(Now(), _pins.Count == 0 ? 0 : _pins.Values.Max() + 1);
            _pins[key] = at;
            repo.SetPin(key, at);
        }
        else
        {
            _pins.Remove(key);
            repo.SetPin(key, null);
        }
        RaiseChanged();
    }

    /// <summary>用户所在的自定义分组；只在“我的好友”中时为 null。</summary>
    public long? GetGroupId(long userId) => _membership.TryGetValue(userId, out var id) ? id : null;

    public string? GetRemark(long userId) => _remarks.GetValueOrDefault(userId);

    /// <summary>备注优先，其次 Zulip 昵称；用户不存在时为“未知用户”。</summary>
    public string DisplayName(long userId) =>
        GetRemark(userId) ?? users.Get(userId)?.FullName ?? "未知用户";

    public int CountInGroup(long groupId) => _membership.Values.Count(g => g == groupId);

    /// <summary>新建分组并追加到末尾。名称允许与已有分组相同。</summary>
    public FriendGroup AddGroup(string name)
    {
        var group = repo.AddGroup(name);
        _groups.Add(group);
        RaiseChanged();
        return group;
    }

    public void RenameGroup(long groupId, string name)
    {
        var index = _groups.FindIndex(g => g.Id == groupId);
        if (index < 0 || _groups[index].Name == name)
            return;
        repo.RenameGroup(groupId, name);
        _groups[index] = _groups[index] with { Name = name };
        RaiseChanged();
    }

    /// <summary>删除分组，其成员回到“我的好友”。</summary>
    public void DeleteGroup(long groupId)
    {
        if (_groups.RemoveAll(g => g.Id == groupId) == 0)
            return;
        repo.DeleteGroup(groupId);
        foreach (var user in _membership.Where(p => p.Value == groupId).Select(p => p.Key).ToList())
            _membership.Remove(user);
        RaiseChanged();
    }

    /// <summary>
    /// 把用户移动到分组；<paramref name="groupId"/> 为 null 表示移回“我的好友”。
    /// 已在目标分组的用户不变，没有任何变化时不触发 <see cref="Changed"/>。
    /// </summary>
    public void MoveUsers(IReadOnlyCollection<long> userIds, long? groupId)
    {
        if (groupId is { } id && _groups.All(g => g.Id != id))
            throw new ArgumentException($"分组 {id} 不存在", nameof(groupId));

        var moving = userIds.Where(u => GetGroupId(u) != groupId).ToList();
        if (moving.Count == 0)
            return;

        repo.SetMembership(moving, groupId);
        foreach (var user in moving)
        {
            if (groupId is { } g)
                _membership[user] = g;
            else
                _membership.Remove(user);
        }
        RaiseChanged();
    }

    /// <summary>设置备注。去掉首尾空白后为空则视为清除备注。备注未变化时不触发 <see cref="Changed"/>。</summary>
    public void SetRemark(long userId, string? remark)
    {
        var normalized = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        if (GetRemark(userId) == normalized)
            return;

        repo.SetRemark(userId, normalized);
        if (normalized is null)
            _remarks.Remove(userId);
        else
            _remarks[userId] = normalized;
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
