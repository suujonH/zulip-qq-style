using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>群聊成员列表中的一个人。<see cref="Badge"/> 为“群主”（频道创建者）或“管理员”（频道管理员）。</summary>
public sealed class ChatMemberItem(long userId, string displayName, string? avatarUrl, string email, string? badge, bool isOwner)
{
    public long UserId { get; } = userId;
    public string DisplayName { get; } = displayName;
    public string? AvatarUrl { get; } = avatarUrl;
    public string Email { get; } = email;
    public string? Badge { get; } = badge;
    public bool HasBadge => Badge is not null;

    /// <summary>群主徽标为橙色，管理员为蓝色。</summary>
    public bool IsOwner { get; } = isOwner;

    public bool Matches(string query) =>
        DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) || Email.Contains(query, StringComparison.OrdinalIgnoreCase);
}

/// <summary>群聊成员列表的排序：群主、管理员在前，其余在线（含离开）的在前，再按名字排序。</summary>
public static class ChatMembers
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    /// <param name="channel">话题所在频道；多人私聊为 null，没有徽标。</param>
    public static List<ChatMemberItem> Build(ZulipSession session, Channel? channel, IEnumerable<long> userIds)
    {
        var now = DateTimeOffset.UtcNow;
        return userIds.Distinct()
            .Select(id => session.Users.Get(id))
            .OfType<ZulipUser>()
            .Select(u =>
            {
                var rank = channel is null ? 2
                    : u.Id == channel.CreatorId ? 0
                    : u.Role <= PermissionEvaluator.RoleAdministrator
                      || session.Permissions.UserAllows(u.Id, channel.CanAdministerChannelGroup) ? 1
                    : 2;
                var online = session.Users.GetPresence(u.Id, now) != PresenceState.Offline;
                var item = new ChatMemberItem(u.Id, session.Contacts.DisplayName(u.Id), u.AvatarUrl, u.Email,
                    rank switch { 0 => "群主", 1 => "管理员", _ => null }, rank == 0);
                return (Item: item, Rank: rank, Online: online);
            })
            .OrderBy(x => x.Rank)
            .ThenByDescending(x => x.Rank == 2 && x.Online)
            .ThenBy(x => x.Item.DisplayName, NameComparer)
            .Select(x => x.Item)
            .ToList();
    }
}
