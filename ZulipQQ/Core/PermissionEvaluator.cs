using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Core;

/// <summary>频道级的组设置，缺失的字段为 null。</summary>
public interface IChannelPermissions
{
    GroupSettingValue? CanAdministerChannelGroup { get; }
    GroupSettingValue? CanAddSubscribersGroup { get; }
    GroupSettingValue? CanRemoveSubscribersGroup { get; }
    GroupSettingValue? CanSendMessageGroup { get; }
    GroupSettingValue? CanMoveMessagesOutOfChannelGroup { get; }
    GroupSettingValue? CanMoveMessagesWithinChannelGroup { get; }
    GroupSettingValue? CanResolveTopicsGroup { get; }
    GroupSettingValue? CanCreateTopicGroup { get; }
}

/// <summary>构造 <see cref="PermissionEvaluator"/> 所需的数据。</summary>
/// <param name="Role">100 所有者 / 200 管理员 / 300 版主 / 400 成员 / 600 访客。</param>
/// <param name="RealmGroupSettings">组织级组设置，键不带 <c>realm_</c> 前缀。</param>
/// <param name="AllowEveryoneGroup">来自 <c>server_supported_permission_settings</c>：该设置是否允许“所有人”组（含访客）。</param>
/// <param name="EditLimitSeconds">消息编辑时限；null 表示不限。</param>
public sealed record PermissionInputs(
    long SelfId,
    int Role,
    IReadOnlyDictionary<long, UserGroupDto> Groups,
    IReadOnlyDictionary<string, GroupSettingValue> RealmGroupSettings,
    IReadOnlyDictionary<(string Scope, string Name), bool> AllowEveryoneGroup,
    bool AllowMessageEditing,
    int? EditLimitSeconds);

/// <summary>
/// 根据 Zulip 的组设置判断当前用户的权限。没有权限的功能在界面上完全不显示。
/// 收到 realm、user_group 事件或自己的角色变化后，整体重新构造。
/// </summary>
public sealed class PermissionEvaluator(PermissionInputs inputs)
{
    public const int RoleOwner = 100;
    public const int RoleAdministrator = 200;
    public const int RoleModerator = 300;
    public const int RoleMember = 400;
    public const int RoleGuest = 600;

    private const string Realm = "realm";
    private const string Stream = "stream";

    private readonly Dictionary<long, bool> _memberCache = [];

    public long SelfId => inputs.SelfId;

    public int Role => inputs.Role;

    public bool IsGuest => inputs.Role >= RoleGuest;

    /// <summary>组织所有者或管理员。</summary>
    public bool IsOrgAdmin => inputs.Role <= RoleAdministrator;

    public static PermissionEvaluator FromRegister(RegisterResult reg) =>
        new(new PermissionInputs(
            reg.UserId,
            reg.SelfRole(),
            reg.RealmUserGroups.ToDictionary(g => g.Id),
            reg.RealmGroupSettings(),
            reg.AllowEveryoneGroup(),
            reg.RealmAllowMessageEditing,
            reg.RealmMessageContentEditLimitSeconds));

    /// <summary>自己是否属于该组（递归检查子组）。</summary>
    public bool IsMember(long groupId)
    {
        if (_memberCache.TryGetValue(groupId, out var cached))
            return cached;
        var result = UserIsMember(inputs.SelfId, groupId);
        _memberCache[groupId] = result;
        return result;
    }

    /// <summary>任意用户是否属于该组（递归检查子组，防止循环）。</summary>
    public bool UserIsMember(long userId, long groupId)
    {
        HashSet<long> visited = [];
        Stack<long> pending = new([groupId]);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!visited.Add(id) || !inputs.Groups.TryGetValue(id, out var group))
                continue;
            if (group.Members.Contains(userId))
                return true;
            foreach (var sub in group.DirectSubgroupIds)
                pending.Push(sub);
        }
        return false;
    }

    /// <summary>任意用户是否满足该组设置值（不考虑访客限制）。</summary>
    public bool UserAllows(long userId, GroupSettingValue? value)
    {
        if (value is null)
            return false;
        if (value.GroupId is { } groupId)
            return UserIsMember(userId, groupId);
        return value.DirectMembers.Contains(userId) || value.DirectSubgroups.Any(g => UserIsMember(userId, g));
    }

    /// <summary>自己是否满足该组设置值。访客对不允许“所有人”组的设置一律没有权限。</summary>
    public bool Allows(GroupSettingValue? value, string scope, string name)
    {
        if (value is null)
            return false;
        if (IsGuest && inputs.AllowEveryoneGroup.TryGetValue((scope, name), out var allowEveryone) && !allowEveryone)
            return false;
        if (value.GroupId is { } groupId)
            return IsMember(groupId);
        return value.DirectMembers.Contains(inputs.SelfId) || value.DirectSubgroups.Any(IsMember);
    }

    /// <summary>组织级设置是否允许自己。</summary>
    public bool AllowsRealm(string name) =>
        Allows(inputs.RealmGroupSettings.GetValueOrDefault(name), Realm, name);

    public bool CanAdministerChannel(IChannelPermissions ch) =>
        IsOrgAdmin || Allows(ch.CanAdministerChannelGroup, Stream, "can_administer_channel_group");

    public bool CanMoveTopicWithinChannel(IChannelPermissions ch) =>
        AllowsRealm("can_move_messages_between_topics_group")
        || Allows(ch.CanMoveMessagesWithinChannelGroup, Stream, "can_move_messages_within_channel_group")
        || CanAdministerChannel(ch);

    public bool CanMoveTopicBetweenChannels(IChannelPermissions ch) =>
        AllowsRealm("can_move_messages_between_channels_group")
        || Allows(ch.CanMoveMessagesOutOfChannelGroup, Stream, "can_move_messages_out_of_channel_group")
        || CanAdministerChannel(ch);

    public bool CanResolveTopic(IChannelPermissions ch) =>
        AllowsRealm("can_resolve_topics_group")
        || Allows(ch.CanResolveTopicsGroup, Stream, "can_resolve_topics_group");

    /// <summary>删除话题：Zulip 文档说明只有组织管理员可以。</summary>
    public bool CanDeleteTopic(IChannelPermissions ch) => IsOrgAdmin;

    public bool CanSendToChannel(IChannelPermissions ch) =>
        ch.CanSendMessageGroup is null || Allows(ch.CanSendMessageGroup, Stream, "can_send_message_group");

    public bool CanCreateTopic(IChannelPermissions ch) =>
        ch.CanCreateTopicGroup is null || Allows(ch.CanCreateTopicGroup, Stream, "can_create_topic_group");

    /// <summary>
    /// 自己加入频道：公开频道非访客都可以；否则需要在 can_subscribe_group 或频道的 can_add_subscribers_group 中。
    /// 私有频道的管理权限不包含加入权限（Zulip 将两者分开），所以组织管理员、频道管理员也不例外。
    /// </summary>
    public bool CanSubscribe(IChannelPermissions ch, bool inviteOnly, GroupSettingValue? canSubscribeGroup) =>
        (!inviteOnly && !IsGuest)
        || Allows(canSubscribeGroup, Stream, "can_subscribe_group")
        || Allows(ch.CanAddSubscribersGroup, Stream, "can_add_subscribers_group");

    public bool CanAddSubscribers(IChannelPermissions ch) =>
        AllowsRealm("can_add_subscribers_group")
        || Allows(ch.CanAddSubscribersGroup, Stream, "can_add_subscribers_group")
        || CanAdministerChannel(ch);

    /// <summary>频道管理员也可以移出成员（feature level 349 起）。</summary>
    public bool CanRemoveSubscribers(IChannelPermissions ch) =>
        IsOrgAdmin
        || Allows(ch.CanRemoveSubscribersGroup, Stream, "can_remove_subscribers_group")
        || CanAdministerChannel(ch);

    public bool CanCreatePublicChannel => AllowsRealm("can_create_public_channel_group");

    public bool CanCreatePrivateChannel => AllowsRealm("can_create_private_channel_group");

    public bool CanInviteUsers => AllowsRealm("can_invite_users_group");

    public bool CanMentionEveryone => AllowsRealm("can_mention_many_users_group");

    /// <summary>自己的消息，且组织允许编辑，且在编辑时限内（时间均为 Unix 秒）。</summary>
    public bool CanEditMessage(long senderId, long messageTimestamp, long now) =>
        senderId == inputs.SelfId
        && inputs.AllowMessageEditing
        && (inputs.EditLimitSeconds is not { } limit || now - messageTimestamp < limit);
}
