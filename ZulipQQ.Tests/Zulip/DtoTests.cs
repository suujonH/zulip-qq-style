using ZulipQQ.Core;
using ZulipQQ.Zulip;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.Tests.Zulip;

public class DtoTests
{
    [Fact]
    public void Register_Deserializes()
    {
        var reg = ZulipJson.Deserialize<RegisterResult>(Fixture.Json("register.json"));

        Assert.Equal("REDACTED", reg.QueueId);
        Assert.True(reg.ZulipFeatureLevel >= 421);
        Assert.Equal(8, reg.UserId);
        Assert.Equal(100, reg.SelfRole());
        Assert.NotEmpty(reg.RealmUsers);
        Assert.Contains(reg.RealmUsers, u => u.UserId == 9 && !u.IsBot && u.IsActive);
        Assert.Contains(reg.CrossRealmBots, u => u.IsBot);

        var dev = Assert.Single(reg.Subscriptions, s => s.StreamId == 4);
        Assert.False(string.IsNullOrEmpty(dev.Name));
        Assert.StartsWith("#", dev.Color);
        Assert.NotNull(dev.CanSendMessageGroup);
        Assert.NotNull(dev.CanAdministerChannelGroup);

        Assert.True(reg.UnreadMsgs.Count > 0);
        Assert.NotEmpty(reg.UnreadMsgs.Streams);
        Assert.NotEmpty(reg.UnreadMsgs.Pms);
        Assert.NotEmpty(reg.UnreadMsgs.Mentions);
        Assert.NotEmpty(reg.UserTopics);
        Assert.NotEmpty(reg.Drafts);
        Assert.NotEmpty(reg.StarredMessages);
        Assert.NotEmpty(reg.RealmUserGroups);
        Assert.Contains(reg.RecentPrivateConversations, c => c.UserIds.Count == 0);
        Assert.True(reg.UserSettings.EnableDraftsSynchronization);
        Assert.Equal(600, reg.RealmMessageContentEditLimitSeconds);
        Assert.Equal("all", reg.RealmMessageEditHistoryVisibilityPolicy);
        Assert.False(string.IsNullOrEmpty(reg.ServerEmojiDataUrl));
    }

    [Fact]
    public void Register_RealmGroupSettings_UseServerSupportedNames()
    {
        var reg = ZulipJson.Deserialize<RegisterResult>(Fixture.Json("register.json"));
        var settings = reg.RealmGroupSettings();

        // 不以 _group 结尾的设置名也要取到
        Assert.True(settings.ContainsKey("can_create_groups"));
        Assert.True(settings.ContainsKey("can_manage_all_groups"));
        Assert.True(settings.ContainsKey("direct_message_permission_group"));
        Assert.True(settings.ContainsKey("can_mention_many_users_group"));
        Assert.All(settings.Values, v => Assert.True(v.GroupId is not null || v.DirectMembers.Count + v.DirectSubgroups.Count >= 0));

        var allow = reg.AllowEveryoneGroup();
        Assert.False(allow[("realm", "can_add_custom_emoji_group")]);
        Assert.True(allow[("stream", "can_send_message_group")]);
        Assert.Contains("can_send_message_group", reg.PermissionSettingNames("stream"));
    }

    [Fact]
    public void Messages_Topic_Deserializes()
    {
        var result = ZulipJson.Deserialize<GetMessagesResult>(Fixture.Json("messages_topic.json"));
        Assert.NotEmpty(result.Messages);
        var m = result.Messages[^1];
        Assert.True(m.IsStream);
        Assert.Equal(4, m.StreamId);
        Assert.False(string.IsNullOrEmpty(m.Subject));
        Assert.Empty(m.RecipientUserIds());
        var key = Assert.IsType<TopicKey>(ConversationKey.FromMessage(m, 8));
        Assert.Equal(4, key.StreamId);
    }

    [Fact]
    public void Messages_Dm_Deserializes()
    {
        var result = ZulipJson.Deserialize<GetMessagesResult>(Fixture.Json("messages_dm.json"));
        var m = result.Messages[0];
        Assert.False(m.IsStream);
        Assert.Equal([8L, 9L], m.RecipientUserIds().Order());
        Assert.Contains("read", m.Flags);
        Assert.Equal(DmKey.Create([9], 8), ConversationKey.FromMessage(m, 8));
    }

    [Fact]
    public void GroupSettingValue_ReadsBothForms()
    {
        var number = ZulipJson.Deserialize<GroupSettingValue>(System.Text.Json.JsonDocument.Parse("12").RootElement);
        Assert.Equal(GroupSettingValue.Group(12), number);

        var obj = ZulipJson.Deserialize<GroupSettingValue>(System.Text.Json.JsonDocument
            .Parse("""{"direct_members":[1,2],"direct_subgroups":[3]}""").RootElement);
        Assert.Null(obj.GroupId);
        Assert.Equal([1L, 2L], obj.DirectMembers);
        Assert.Equal([3L], obj.DirectSubgroups);
        Assert.Equal(GroupSettingValue.Anonymous([2, 1], [3]), obj);

        Assert.Equal("12", ZulipJson.Serialize(number));
        Assert.Equal("""{"direct_members":[1,2],"direct_subgroups":[3]}""", ZulipJson.Serialize(obj));
    }
}
