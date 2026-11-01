using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class PushEffectTests
{
    private const PushTriggers M = PushTriggers.Mention;
    private const PushTriggers W = PushTriggers.Wildcard;
    private const PushTriggers A = PushTriggers.AllMessages;

    private static readonly PushRules Defaults = new(DmAndMention: true, Stream: false, FollowedTopic: true,
        WildcardMentions: true, FollowedTopicWildcardMentions: true);

    [Theory]
    [InlineData(false, null, null, TopicVisibility.Inherit, M | W)]
    [InlineData(false, null, null, TopicVisibility.Muted, M)]
    [InlineData(false, null, null, TopicVisibility.Followed, M | W | A)]
    [InlineData(true, null, null, TopicVisibility.Inherit, M)]
    // 频道上单独设置的 @全体 可以越过频道静音，但不能越过话题静音
    [InlineData(true, null, true, TopicVisibility.Inherit, M | W)]
    [InlineData(true, null, true, TopicVisibility.Muted, M)]
    [InlineData(true, null, null, TopicVisibility.Unmuted, M | W)]
    // 频道静音时“全部消息”来自关注的话题，而不是频道设置
    [InlineData(true, true, null, TopicVisibility.Followed, M | W | A)]
    [InlineData(true, true, null, TopicVisibility.Inherit, M)]
    [InlineData(false, true, null, TopicVisibility.Inherit, M | W | A)]
    [InlineData(false, null, false, TopicVisibility.Inherit, M)]
    public void Evaluate(bool channelMuted, bool? channelAll, bool? channelWildcard, int policy, PushTriggers expected) =>
        Assert.Equal(expected, PushEffect.Evaluate(Defaults, channelMuted, channelAll, channelWildcard, policy));

    [Fact]
    public void ChannelSettingOverridesGlobal()
    {
        var rules = Defaults with { Stream = true };
        Assert.Equal(M | W | A, PushEffect.Evaluate(rules, false, null, null, TopicVisibility.Inherit));
        Assert.Equal(M | W, PushEffect.Evaluate(rules, false, false, null, TopicVisibility.Inherit));
    }

    [Fact]
    public void WildcardRequiresDmAndMention()
    {
        var rules = Defaults with { DmAndMention = false };
        Assert.Equal(PushTriggers.None, PushEffect.Evaluate(rules, false, null, true, TopicVisibility.Inherit));
        Assert.Equal(A, PushEffect.Evaluate(rules, false, null, null, TopicVisibility.Followed));
    }

    [Theory]
    [InlineData(PushTriggers.None, "无")]
    [InlineData(M | A, "@我、全部消息")]
    [InlineData(M | W | A, "@我、@全体、全部消息")]
    public void Describe(PushTriggers triggers, string expected) =>
        Assert.Equal(expected, PushEffect.Describe(triggers));
}
