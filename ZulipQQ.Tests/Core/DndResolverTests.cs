using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class DndResolverTests
{
    [Theory]
    [InlineData(false, TopicVisibility.Inherit, false)]
    [InlineData(false, TopicVisibility.Muted, true)]
    [InlineData(false, TopicVisibility.Followed, false)]
    [InlineData(true, TopicVisibility.Inherit, true)]
    [InlineData(true, TopicVisibility.Muted, true)]
    [InlineData(true, TopicVisibility.Unmuted, false)]
    [InlineData(true, TopicVisibility.Followed, false)]
    public void IsZulipMuted(bool channelMuted, int policy, bool expected) =>
        Assert.Equal(expected, DndResolver.IsZulipMuted(channelMuted, policy));

    [Theory]
    [InlineData(null, false, DndLevel.Notify)]
    [InlineData(null, true, DndLevel.Silent)]
    [InlineData(DndLevel.Notify, true, DndLevel.Silent)]
    [InlineData(DndLevel.Notify, false, DndLevel.Notify)]
    [InlineData(DndLevel.Silent, false, DndLevel.Notify)]
    [InlineData(DndLevel.Assistant, false, DndLevel.Notify)]
    [InlineData(DndLevel.Blocked, false, DndLevel.Notify)]
    [InlineData(DndLevel.Assistant, true, DndLevel.Assistant)]
    [InlineData(DndLevel.Blocked, true, DndLevel.Blocked)]
    public void Resolve(DndLevel? stored, bool muted, DndLevel expected) =>
        Assert.Equal(expected, DndResolver.Resolve(stored, muted));

    [Fact]
    public void ResolveLocalOnly_DefaultsToNotify()
    {
        Assert.Equal(DndLevel.Notify, DndResolver.ResolveLocalOnly(null));
        Assert.Equal(DndLevel.Assistant, DndResolver.ResolveLocalOnly(DndLevel.Assistant));
    }

    [Theory]
    [InlineData(DndLevel.Notify, false, TopicVisibility.Muted, TopicVisibility.Inherit)]
    [InlineData(DndLevel.Notify, true, TopicVisibility.Muted, TopicVisibility.Unmuted)]
    [InlineData(DndLevel.Notify, true, TopicVisibility.Inherit, TopicVisibility.Unmuted)]
    [InlineData(DndLevel.Notify, false, TopicVisibility.Inherit, null)]
    [InlineData(DndLevel.Notify, true, TopicVisibility.Followed, null)]
    [InlineData(DndLevel.Notify, false, TopicVisibility.Unmuted, null)]
    [InlineData(DndLevel.Silent, false, TopicVisibility.Inherit, TopicVisibility.Muted)]
    [InlineData(DndLevel.Assistant, true, TopicVisibility.Inherit, TopicVisibility.Muted)]
    [InlineData(DndLevel.Blocked, true, TopicVisibility.Followed, TopicVisibility.Muted)]
    [InlineData(DndLevel.Silent, true, TopicVisibility.Muted, null)]
    public void PolicyToApply(DndLevel target, bool channelMuted, int current, int? expected) =>
        Assert.Equal(expected, DndResolver.PolicyToApply(target, channelMuted, current));

    [Fact]
    public void PolicyToApply_ThenResolve_IsConsistent()
    {
        foreach (var target in Enum.GetValues<DndLevel>())
        foreach (var channelMuted in new[] { false, true })
        foreach (var current in new[] { 0, 1, 2, 3 })
        {
            var policy = DndResolver.PolicyToApply(target, channelMuted, current) ?? current;
            var muted = DndResolver.IsZulipMuted(channelMuted, policy);
            Assert.Equal(target, DndResolver.Resolve(target, muted));
        }
    }
}
