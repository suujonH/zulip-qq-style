using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public class ForwardFormatterTests
{
    private static readonly DateTime Time = new(2026, 10, 22, 9, 5, 0);

    [Fact]
    public void Merge_FormatsEachMessage()
    {
        var result = ForwardFormatter.Merge([new ForwardedMessage("阿杰", Time, "你好"), new ForwardedMessage("小雨", Time, "在吗\n")], 0);
        Assert.Equal(
            "**聊天记录**\n**阿杰** 2026/10/22 09:05\n```quote\n你好\n```\n**小雨** 2026/10/22 09:05\n```quote\n在吗\n```",
            Assert.Single(result));
    }

    [Fact]
    public void Merge_LengthensFenceForCode()
    {
        var result = ForwardFormatter.Merge([new ForwardedMessage("阿杰", Time, "```\ncode\n```")], 0);
        Assert.Equal("**聊天记录**\n**阿杰** 2026/10/22 09:05\n````quote\n```\ncode\n```\n````", Assert.Single(result));
    }

    [Fact]
    public void Merge_SplitsAtMessageBoundaries()
    {
        var messages = Enumerable.Range(0, 3).Select(i => new ForwardedMessage("阿杰", Time, new string('a', 40))).ToList();
        var result = ForwardFormatter.Merge(messages, 150);
        Assert.Equal(3, result.Count);
        Assert.All(result, r =>
        {
            Assert.StartsWith(ForwardFormatter.Header, r);
            Assert.True(r.Length <= 150);
        });
    }

    [Fact]
    public void Merge_TruncatesSingleOversizedMessage()
    {
        var result = Assert.Single(ForwardFormatter.Merge([new ForwardedMessage("阿杰", Time, new string('a', 500))], 120));
        Assert.True(result.Length <= 120);
        Assert.Contains("a…\n```", result);
    }

    [Fact]
    public void Merge_Empty() => Assert.Empty(ForwardFormatter.Merge([], 100));
}
