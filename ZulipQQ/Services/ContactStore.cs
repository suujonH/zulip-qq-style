using System;
using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Data;
using ZulipQQ.Models;

namespace ZulipQQ.Services;

/// <summary>
/// 好友分组与好友备注（只保存在本地）。联系人页、好友管理器都从这里读取，所有修改也都经过这里，
/// 修改后触发 <see cref="Changed"/>，各界面据此刷新。“我的好友”是虚拟分组，显示全部用户，不在 <see cref="Groups"/> 中。
/// </summary>
public sealed class ContactStore(ContactRepository repo, UserStore users)
{
    private readonly List<FriendGroup> _groups = [];
    private readonly Dictionary<long, long> _membership = [];
    private readonly Dictionary<long, string> _remarks = [];

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
