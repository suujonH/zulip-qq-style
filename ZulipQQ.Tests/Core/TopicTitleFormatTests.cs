using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class TopicTitleFormatTests
{
    [Theory]
    [InlineData("{Topic}", "人事通知")]
    [InlineData("{Channel} / {Topic}", "公告 / 人事通知")]
    [InlineData("[{channel}] {TOPIC}", "[公告] 人事通知")]
    [InlineData("#{Channel}", "#公告")]
    [InlineData("{Topic}{Topic}", "人事通知人事通知")]
    [InlineData("{Unknown} {Topic}", "{Unknown} 人事通知")]
    [InlineData("  {Topic}  ", "人事通知")]
    public void Format(string template, string expected) =>
        Assert.Equal(expected, TopicTitleFormat.Format(template, "公告", "人事通知"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankTemplate_UsesTopic(string? template) =>
        Assert.Equal("人事通知", TopicTitleFormat.Format(template, "公告", "人事通知"));

    [Fact]
    public void BlankResult_UsesTopic() => Assert.Equal("人事通知", TopicTitleFormat.Format("{Channel}", "", "人事通知"));

    // 频道名、话题名中恰好含有变量文字时不再二次替换
    [Fact]
    public void ValuesAreNotExpanded() =>
        Assert.Equal("{Topic} / x", TopicTitleFormat.Format("{Channel} / {Topic}", "{Topic}", "x"));
}
