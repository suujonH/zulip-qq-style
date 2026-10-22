using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class HighlightTextTests
{
    [Fact]
    public void Split_MarksEveryMatchIgnoringCase()
    {
        var parts = HighlightText.Split("Dev 开发 dev", "DEV");
        Assert.Equal([("Dev", true), (" 开发 ", false), ("dev", true)], parts);
    }

    [Fact]
    public void Split_NoQueryOrNoMatch_ReturnsWholeText()
    {
        Assert.Equal([("阿杰", false)], HighlightText.Split("阿杰", null));
        Assert.Equal([("阿杰", false)], HighlightText.Split("阿杰", "雨"));
        Assert.Empty(HighlightText.Split("", "a"));
    }
}
