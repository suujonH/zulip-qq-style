using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class WindowBoundsTests
{
    [Fact]
    public void RoundTrip()
    {
        var b = new WindowBounds(-1200, 35, 374.5, 694);
        Assert.Equal("-1200,35,374.5,694", b.ToString());
        Assert.Equal(b, WindowBounds.Parse(b.ToString()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1,2,3")]
    [InlineData("a,2,3,4")]
    [InlineData("1,2,0,4")]
    [InlineData("1,2,3,-4")]
    [InlineData("1,2,NaN,4")]
    public void ParseInvalid(string? s) => Assert.Null(WindowBounds.Parse(s));
}
