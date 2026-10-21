using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class ZulipLinksTests
{
    private static readonly Uri Realm = new("https://localhost.localdomain:8443/");

    [Theory]
    [InlineData("构建失败")]
    [InlineData("a b")]
    [InlineData("v1.2.3")]
    [InlineData("f(x)")]
    [InlineData("✔ 代码评审")]
    [InlineData("100%")]
    public void HashComponent_RoundTrip(string s) =>
        Assert.Equal(s, ZulipLinks.DecodeHashComponent(ZulipLinks.EncodeHashComponent(s)));

    [Theory]
    [InlineData("a.b", "a.2Eb")]
    [InlineData("f(x)", "f.28x.29")]
    [InlineData("a b", "a.20b")]
    [InlineData("开", ".E5.BC.80")]
    public void EncodeHashComponent_MatchesWebApp(string s, string expected) =>
        Assert.Equal(expected, ZulipLinks.EncodeHashComponent(s));

    [Fact]
    public void TopicLink_And_Parse()
    {
        var link = ZulipLinks.TopicLink(Realm, 4, "开发 组", "构建 失败(1).log");
        Assert.StartsWith("https://localhost.localdomain:8443/#narrow/channel/4-", link);

        Assert.True(ZulipLinks.TryParse(link + "/near/123", Realm, out var parsed));
        Assert.Equal(4, parsed.StreamId);
        Assert.Equal("构建 失败(1).log", parsed.Topic);
        Assert.Equal(123, parsed.NearMessageId);
    }

    [Fact]
    public void MessageLink_Dm_IncludesSelfSorted()
    {
        var link = ZulipLinks.MessageLink(Realm, DmKey.Create([12, 9], 8), 8, 55, "", "");
        Assert.Equal("https://localhost.localdomain:8443/#narrow/dm/8,9,12-dm/near/55", link);

        Assert.True(ZulipLinks.TryParse(link, Realm, out var parsed));
        Assert.Equal([8L, 9L, 12L], parsed.DmUserIds);
        Assert.Equal(55, parsed.NearMessageId);
    }

    [Fact]
    public void TryParse_RelativeAndLegacyStream()
    {
        Assert.True(ZulipLinks.TryParse("#narrow/stream/5-games/topic/abc/with/7", Realm, out var parsed));
        Assert.Equal(5, parsed.StreamId);
        Assert.Equal("abc", parsed.Topic);
        Assert.Equal(7, parsed.NearMessageId);
    }

    [Theory]
    [InlineData("https://other.example.com/#narrow/channel/4-x/topic/y")]
    [InlineData("https://localhost.localdomain:8443/#narrow/channel/4-x")]
    [InlineData("https://example.com/page")]
    [InlineData("#narrow/is/starred")]
    public void TryParse_Rejects(string url) => Assert.False(ZulipLinks.TryParse(url, Realm, out _));
}
