using System;
using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>
/// 好友与分组的唯一数据源。联系人页、好友管理器都从这里读取，所有修改也都经过这里，
/// 修改后触发 <see cref="Changed"/>，各界面据此刷新。
/// </summary>
public sealed class FriendStore
{
    private readonly List<FriendGroup> _groups;
    private readonly List<Friend> _friends;

    public FriendStore(IEnumerable<FriendGroup> groups, IEnumerable<Friend> friends)
    {
        _groups = groups.ToList();
        _friends = friends.ToList();
    }

    /// <summary>当前使用的实例。接入 Zulip 前使用假数据。</summary>
    public static FriendStore Instance { get; } = new(MockData.FriendGroups, MockData.Friends);

    public event EventHandler? Changed;

    /// <summary>按创建顺序排列的分组。</summary>
    public IReadOnlyList<FriendGroup> Groups => _groups;

    public IReadOnlyList<Friend> Friends => _friends;

    public int CountInGroup(long groupId) => _friends.Count(f => f.GroupId == groupId);

    /// <summary>新建分组并追加到末尾。名称允许与已有分组相同。</summary>
    public FriendGroup AddGroup(string name)
    {
        var group = new FriendGroup(_groups.Count == 0 ? 1 : _groups.Max(g => g.Id) + 1, name);
        _groups.Add(group);
        RaiseChanged();
        return group;
    }

    /// <summary>把指定好友移动到分组；已在该分组的好友不变。没有任何变化时不触发 <see cref="Changed"/>。</summary>
    public void MoveFriends(IReadOnlyCollection<long> userIds, long groupId)
    {
        if (_groups.All(g => g.Id != groupId))
            throw new ArgumentException($"分组 {groupId} 不存在", nameof(groupId));

        var changed = false;
        for (var i = 0; i < _friends.Count; i++)
        {
            if (_friends[i].GroupId != groupId && userIds.Contains(_friends[i].User.Id))
            {
                _friends[i] = _friends[i] with { GroupId = groupId };
                changed = true;
            }
        }

        if (changed)
            RaiseChanged();
    }

    /// <summary>设置备注。去掉首尾空白后为空则视为清除备注。备注未变化时不触发 <see cref="Changed"/>。</summary>
    public void SetRemark(long userId, string? remark)
    {
        var normalized = string.IsNullOrWhiteSpace(remark) ? null : remark.Trim();
        var index = _friends.FindIndex(f => f.User.Id == userId);
        if (index < 0 || _friends[index].Remark == normalized)
            return;

        _friends[index] = _friends[index] with { Remark = normalized };
        RaiseChanged();
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
