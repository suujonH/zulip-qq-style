using ZulipQQ.Core;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Tests.Core;

public class PermissionEvaluatorTests
{
    // 系统组：9 nobody，10 owners，11 administrators，12 moderators，13 fullmembers，14 members，15 everyone
    private static readonly Dictionary<long, UserGroupDto> Groups = new()
    {
        [9] = new() { Id = 9, Name = "role:nobody", IsSystemGroup = true },
        [10] = new() { Id = 10, Name = "role:owners", Members = [8], IsSystemGroup = true },
        [11] = new() { Id = 11, Name = "role:administrators", Members = [20], DirectSubgroupIds = [10], IsSystemGroup = true },
        [12] = new() { Id = 12, Name = "role:moderators", DirectSubgroupIds = [11], IsSystemGroup = true },
        [13] = new() { Id = 13, Name = "role:fullmembers", Members = [9], DirectSubgroupIds = [12], IsSystemGroup = true },
        [14] = new() { Id = 14, Name = "role:members", Members = [9, 10], DirectSubgroupIds = [13], IsSystemGroup = true },
        [15] = new() { Id = 15, Name = "role:everyone", Members = [30], DirectSubgroupIds = [14], IsSystemGroup = true },
        // 循环引用的自定义组
        [50] = new() { Id = 50, Name = "loop-a", DirectSubgroupIds = [51] },
        [51] = new() { Id = 51, Name = "loop-b", DirectSubgroupIds = [50], Members = [10] },
    };

    private sealed record Channel : IChannelPermissions
    {
        public GroupSettingValue? CanAdministerChannelGroup { get; init; }
        public GroupSettingValue? CanAddSubscribersGroup { get; init; }
        public GroupSettingValue? CanRemoveSubscribersGroup { get; init; }
        public GroupSettingValue? CanSendMessageGroup { get; init; }
        public GroupSettingValue? CanMoveMessagesOutOfChannelGroup { get; init; }
        public GroupSettingValue? CanMoveMessagesWithinChannelGroup { get; init; }
        public GroupSettingValue? CanResolveTopicsGroup { get; init; }
        public GroupSettingValue? CanCreateTopicGroup { get; init; }
    }

    private static PermissionEvaluator Create(long self, int role, Dictionary<string, GroupSettingValue>? realm = null,
        Dictionary<(string, string), bool>? allowEveryone = null, int? editLimit = 600) =>
        new(new PermissionInputs(self, role, Groups, realm ?? [], allowEveryone ?? [], true, editLimit));

    [Fact]
    public void IsMember_Recursive()
    {
        var owner = Create(8, 100);
        Assert.True(owner.IsMember(10));
        Assert.True(owner.IsMember(14));
        Assert.True(owner.IsMember(15));
        Assert.False(owner.IsMember(9));

        var member = Create(10, 400);
        Assert.True(member.IsMember(14));
        Assert.False(member.IsMember(13));
        Assert.False(member.IsMember(11));
    }

    [Fact]
    public void IsMember_CycleTerminates()
    {
        Assert.True(Create(10, 400).IsMember(50));
        Assert.False(Create(9, 400).IsMember(50));
    }

    [Fact]
    public void Allows_AnonymousGroup()
    {
        var p = Create(30, 400);
        Assert.True(p.Allows(GroupSettingValue.Anonymous([30], []), "stream", "x"));
        Assert.True(Create(9, 400).Allows(GroupSettingValue.Anonymous([], [13]), "stream", "x"));
        Assert.False(Create(10, 400).Allows(GroupSettingValue.Anonymous([30], [13]), "stream", "x"));
        Assert.False(p.Allows(null, "stream", "x"));
    }

    [Fact]
    public void Guest_DeniedWhenEveryoneNotAllowed()
    {
        var allow = new Dictionary<(string, string), bool>
        {
            [("realm", "can_add_custom_emoji_group")] = false,
            [("stream", "can_send_message_group")] = true,
        };
        var guest = Create(30, 600, allowEveryone: allow);
        Assert.False(guest.Allows(GroupSettingValue.Group(15), "realm", "can_add_custom_emoji_group"));
        Assert.True(guest.Allows(GroupSettingValue.Group(15), "stream", "can_send_message_group"));
        Assert.True(Create(30, 400, allowEveryone: allow).Allows(GroupSettingValue.Group(15), "realm", "can_add_custom_emoji_group"));
    }

    [Fact]
    public void ChannelPermissions()
    {
        var realm = new Dictionary<string, GroupSettingValue>
        {
            ["can_move_messages_between_topics_group"] = GroupSettingValue.Group(14),
            ["can_move_messages_between_channels_group"] = GroupSettingValue.Group(12),
            ["can_resolve_topics_group"] = GroupSettingValue.Group(9),
            ["can_add_subscribers_group"] = GroupSettingValue.Group(9),
        };
        var ch = new Channel
        {
            CanAdministerChannelGroup = GroupSettingValue.Anonymous([10], []),
            CanSendMessageGroup = GroupSettingValue.Group(13),
            CanRemoveSubscribersGroup = GroupSettingValue.Group(11),
            CanResolveTopicsGroup = GroupSettingValue.Anonymous([9], []),
        };

        var member = Create(9, 400, realm);
        Assert.True(member.CanMoveTopicWithinChannel(ch));
        Assert.False(member.CanMoveTopicBetweenChannels(ch));
        Assert.True(member.CanResolveTopic(ch));
        Assert.True(member.CanSendToChannel(ch));
        Assert.False(member.CanDeleteTopic(ch));
        Assert.False(member.CanAddSubscribers(ch));
        Assert.False(member.CanRemoveSubscribers(ch));
        Assert.True(member.CanCreateTopic(ch)); // 缺失视为不限制

        var channelAdmin = Create(10, 400, realm);
        Assert.True(channelAdmin.CanAdministerChannel(ch));
        Assert.True(channelAdmin.CanMoveTopicBetweenChannels(ch));
        Assert.True(channelAdmin.CanAddSubscribers(ch));
        Assert.True(channelAdmin.CanRemoveSubscribers(ch));
        Assert.False(channelAdmin.CanSendToChannel(ch));

        var owner = Create(8, 100, realm);
        Assert.True(owner.IsOrgAdmin);
        Assert.True(owner.CanDeleteTopic(ch));
        Assert.True(owner.CanRemoveSubscribers(ch));
    }

    [Fact]
    public void CanSubscribe()
    {
        var ch = new Channel();
        Assert.True(Create(10, 400).CanSubscribe(ch, inviteOnly: false, null));
        Assert.False(Create(30, 600).CanSubscribe(ch, inviteOnly: false, null));
        Assert.False(Create(10, 400).CanSubscribe(ch, inviteOnly: true, null));
        Assert.True(Create(10, 400).CanSubscribe(ch, inviteOnly: true, GroupSettingValue.Anonymous([10], [])));
        Assert.True(Create(8, 100).CanSubscribe(ch, inviteOnly: true, null));
    }

    [Fact]
    public void CanEditMessage()
    {
        var p = Create(9, 400, editLimit: 600);
        Assert.True(p.CanEditMessage(9, 1000, 1599));
        Assert.False(p.CanEditMessage(9, 1000, 1600));
        Assert.False(p.CanEditMessage(10, 1000, 1001));
        Assert.True(Create(9, 400, editLimit: null).CanEditMessage(9, 0, 999999));
    }

    [Fact]
    public void FromRegister_UsesFixture()
    {
        var reg = ZulipJson.Deserialize<RegisterResult>(Fixture.Json("register.json"));
        var p = PermissionEvaluator.FromRegister(reg);
        Assert.Equal(8, p.SelfId);
        Assert.Equal(100, p.Role);
        Assert.True(p.IsOrgAdmin);
        Assert.True(p.CanCreatePublicChannel);
        Assert.True(p.CanInviteUsers);
        Assert.True(p.CanMentionEveryone);

        var dev = reg.Subscriptions.Single(s => s.StreamId == 4);
        Assert.True(p.CanSendToChannel(dev));
        Assert.True(p.CanAdministerChannel(dev));
        Assert.True(p.CanMoveTopicBetweenChannels(dev));
    }
}
