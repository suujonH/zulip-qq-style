using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class SystemNoticeRulesTests
{
    private const long Bot = 6;
    private static readonly HashSet<long> Announcements = [3];

    private static SystemNoticeKind Classify(long sender, long? stream, string topic, string html = "<p>x</p>") =>
        SystemNoticeRules.Classify(Bot, Announcements, sender, stream, topic, html);

    [Fact]
    public void OtherSender_IsNone()
    {
        Assert.Equal(SystemNoticeKind.None, Classify(9, 3, "new channels"));
        Assert.Equal(SystemNoticeKind.None, SystemNoticeRules.Classify(null, Announcements, Bot, null, "", "<p>x</p>"));
    }

    [Fact]
    public void DirectMessages()
    {
        Assert.Equal(SystemNoticeKind.Notice, Classify(Bot, null, ""));
        Assert.Equal(SystemNoticeKind.Reminder,
            Classify(Bot, null, "", "<p>You requested a reminder for the following message.</p>"));
    }

    [Theory]
    [InlineData("new channels")]
    [InlineData("新频道")]
    [InlineData("Signups")]
    [InlineData("Zulip updates")]
    [InlineData("张三 moderation")]
    [InlineData("张三 提到了您：")]
    [InlineData("")]
    public void AnnouncementTopic_IsNotice(string topic) => Assert.Equal(SystemNoticeKind.Notice, Classify(Bot, 3, topic));

    [Theory]
    [InlineData(3, "随便聊")]
    [InlineData(4, "new channels")]
    public void OtherChannelMessages_AreInline(long stream, string topic) =>
        Assert.Equal(SystemNoticeKind.Inline, Classify(Bot, stream, topic));

    [Theory]
    [InlineData(null, "", SystemNoticeCategory.Direct)]
    [InlineData(3L, "New Channels", SystemNoticeCategory.NewChannels)]
    [InlineData(3L, "注册", SystemNoticeCategory.Signups)]
    [InlineData(3L, "Zulip updates", SystemNoticeCategory.ZulipUpdates)]
    [InlineData(3L, "张三 moderation", SystemNoticeCategory.Moderation)]
    [InlineData(3L, "", SystemNoticeCategory.Other)]
    public void CategoryOf(long? stream, string topic, SystemNoticeCategory expected) =>
        Assert.Equal(expected, SystemNoticeRules.CategoryOf(stream, topic));
}
