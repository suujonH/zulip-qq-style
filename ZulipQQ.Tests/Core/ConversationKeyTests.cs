using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class ConversationKeyTests
{
    [Theory]
    [InlineData(4L, "构建失败")]
    [InlineData(5L, "a:b:c")]
    [InlineData(6L, "MixedCase Topic")]
    [InlineData(7L, "")]
    public void TopicKey_RoundTrip(long streamId, string topic)
    {
        var key = new TopicKey(streamId, topic);
        var parsed = ConversationKey.Parse(key.ToStorageString());
        Assert.Equal(key, parsed);
        Assert.Equal(key.GetHashCode(), parsed.GetHashCode());
    }

    [Fact]
    public void TopicKey_IsCaseInsensitive()
    {
        var a = new TopicKey(1, "Hello");
        var b = new TopicKey(1, "hELLO");
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, new TopicKey(2, "Hello"));
        Assert.Equal("t:1:hello", a.ToStorageString());
    }

    [Fact]
    public void TopicKey_TopicWithColons_KeepsRemainder()
    {
        var parsed = (TopicKey)ConversationKey.Parse("t:12:x:y:z");
        Assert.Equal(12, parsed.StreamId);
        Assert.Equal("x:y:z", parsed.Topic);
    }

    [Theory]
    [InlineData(new long[0], "d:")]
    [InlineData(new long[] { 9 }, "d:9")]
    [InlineData(new long[] { 12, 9, 10 }, "d:9,10,12")]
    public void DmKey_RoundTrip(long[] ids, string expected)
    {
        var key = DmKey.Create(ids, selfId: 8);
        Assert.Equal(expected, key.ToStorageString());
        Assert.Equal(key, ConversationKey.Parse(expected));
    }

    [Fact]
    public void DmKey_Create_RemovesSelfDedupesAndSorts()
    {
        var key = DmKey.Create([10, 8, 9, 10], selfId: 8);
        Assert.Equal([9L, 10L], key.OtherUserIds);
        Assert.True(key.IsGroupDm);
        Assert.False(key.IsSelfDm);
        Assert.True(DmKey.Create([8], 8).IsSelfDm);
    }

    [Fact]
    public void DmKey_SeparatelyConstructed_WorkAsDictionaryKeys()
    {
        var dict = new Dictionary<ConversationKey, int> { [DmKey.Create([9, 10], 8)] = 1 };
        Assert.True(dict.ContainsKey(DmKey.Create([10, 9], 8)));
        Assert.True(dict.ContainsKey(ConversationKey.Parse("d:9,10")));
        Assert.False(dict.ContainsKey(DmKey.Create([9], 8)));
    }

    [Fact]
    public void Helper_RoundTrip()
    {
        Assert.Equal("helper", ConversationKey.Helper.ToStorageString());
        Assert.Same(ConversationKey.Helper, ConversationKey.Parse("helper"));
    }

    [Theory]
    [InlineData("x:1")]
    [InlineData("t:abc:topic")]
    [InlineData("t:1")]
    [InlineData("d:1,a")]
    [InlineData("")]
    public void Parse_Invalid_Throws(string s) => Assert.Throws<FormatException>(() => ConversationKey.Parse(s));

    [Fact]
    public void DifferentKinds_AreNotEqual()
    {
        ConversationKey topic = new TopicKey(1, "");
        ConversationKey dm = DmKey.Create([], 8);
        Assert.NotEqual(topic, dm);
        Assert.NotEqual(dm, ConversationKey.Helper);
    }
}
