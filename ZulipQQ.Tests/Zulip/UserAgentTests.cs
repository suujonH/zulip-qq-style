using ZulipQQ.Zulip;

namespace ZulipQQ.Tests.Zulip;

public sealed class UserAgentTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("ZulipQQ")]
    public void ZulipQQ_HasVersion(string? name) => Assert.StartsWith("ZulipQQ/", ZulipClient.UserAgentFor(name));

    // 服务器拒绝低于 5.4.3 的桌面版，报告的版本要足够新
    [Fact]
    public void Desktop_HasSupportedVersion() => Assert.Equal("ZulipElectron/5.12.1", ZulipClient.UserAgentFor("ZulipElectron"));

    [Fact]
    public void Web_IsWebsite() => Assert.Equal("website", ZulipClient.UserAgentFor("website"));

    [Fact]
    public void Custom_TrimmedAndSingleLine() => Assert.Equal("MyBot a", ZulipClient.UserAgentFor("  MyBot\na "));
}
