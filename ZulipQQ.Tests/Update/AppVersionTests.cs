using ZulipQQ.Update;

namespace ZulipQQ.Tests.Update;

public class AppVersionTests
{
    [Theory]
    [InlineData("1.2.3", "1.2.4")]
    [InlineData("1.2.9", "1.3.0")]
    [InlineData("1.9.9", "2.0.0")]
    [InlineData("0.9.0", "0.10.0")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.beta")]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.11")]
    [InlineData("1.0.0-rc.1", "1.0.1-alpha")]
    public void Orders_versions_by_semver(string lower, string higher)
    {
        var a = AppVersion.TryParse(lower)!;
        var b = AppVersion.TryParse(higher)!;
        Assert.True(b.IsNewerThan(a));
        Assert.False(a.IsNewerThan(b));
    }

    [Theory]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.2.3", "1.2.3+abcdef0")]
    [InlineData("1.2.3-beta", "V1.2.3-beta+build.5")]
    public void Prefix_and_build_metadata_are_ignored(string a, string b)
    {
        Assert.Equal(0, AppVersion.TryParse(a)!.CompareTo(AppVersion.TryParse(b)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0.01")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("1.02.3")]
    [InlineData("a.b.c")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-beta..1")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3+")]
    [InlineData("99999999999.0.0")]
    public void Invalid_versions_are_rejected(string? text)
    {
        Assert.Null(AppVersion.TryParse(text));
    }

    [Fact]
    public void Round_trips_without_build_metadata()
    {
        Assert.Equal("1.2.3-beta.1", AppVersion.TryParse("v1.2.3-beta.1+abc")!.ToString());
    }
}
