using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class MentionTextTests
{
    [Theory]
    [InlineData("@", 1, 0, "")]
    [InlineData("你好 @阿", 5, 3, "阿")]
    [InlineData("a\n@jie", 6, 2, "jie")]
    public void TryGetQuery_Found(string text, int caret, int start, string query)
    {
        Assert.True(MentionText.TryGetQuery(text, caret, out var s, out var q));
        Assert.Equal(start, s);
        Assert.Equal(query, q);
    }

    [Theory]
    [InlineData("mail@example", 12)]   // @ 前面不是空白
    [InlineData("@阿 杰", 4)]          // 中间有空白
    [InlineData("@**阿杰|9** ", 11)]   // 已补全
    [InlineData("@*组*", 4)]
    [InlineData("abc", 3)]
    [InlineData("", 0)]
    public void TryGetQuery_NotFound(string text, int caret) =>
        Assert.False(MentionText.TryGetQuery(text, caret, out _, out _));

    [Fact]
    public void Replace_KeepsTextAfterCaret()
    {
        var (text, caret) = MentionText.Replace("hi @a tail", 3, 5, "@**阿杰|9** ");
        Assert.Equal("hi @**阿杰|9**  tail", text);
        Assert.Equal(13, caret);
    }

    [Theory]
    [InlineData("", 0)]
    [InlineData("阿", 0)]
    [InlineData("杰", 1)]
    [InlineData("U01", 0)]
    [InlineData("zz", -1)]
    public void MatchRank(string query, int expected) =>
        Assert.Equal(expected, MentionText.MatchRank(query, "阿杰", null, "u01@zulipqq.test"));
}
