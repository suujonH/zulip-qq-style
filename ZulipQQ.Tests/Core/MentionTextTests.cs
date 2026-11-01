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
    public void TryGetQuery_IgnoresMaskedMention()
    {
        const string text = "@**阿杰|9**";
        Assert.False(MentionText.TryGetQuery(MentionText.Mask(text, [new MentionSpan(0, text.Length, text)]), text.Length, out _, out _));
    }

    [Fact]
    public void Mask_IgnoresSpansOutOfRange() =>
        Assert.Equal("a", MentionText.Mask("a", [new MentionSpan(3, 2, "@**甲|1**")]));

    [Theory]
    [InlineData("", 0)]
    [InlineData("阿", 0)]
    [InlineData("杰", 1)]
    [InlineData("U01", 0)]
    [InlineData("zz", -1)]
    public void MatchRank(string query, int expected) =>
        Assert.Equal(expected, MentionText.MatchRank(query, "阿杰", null, "u01@zulipqq.test"));

    [Theory]
    [InlineData("阿杰|9", false, false, "@阿杰")]
    [InlineData("阿杰|9", true, false, "阿杰")]
    [InlineData("all", false, false, "@全体成员")]
    [InlineData("everyone", false, false, "@全体成员")]
    [InlineData("topic", false, false, "@话题参与者")]
    [InlineData("无编号", false, false, "@无编号")]
    [InlineData("前端组", false, true, "@前端组")]
    public void Display(string name, bool silent, bool isGroup, string expected) =>
        Assert.Equal(expected, MentionText.Display(name, silent, isGroup));

    [Theory]
    [InlineData("@**all** 开会", false, "开会")]
    [InlineData("a @**everyone** b @**channel**", false, "a b ")]
    [InlineData("@**topic** @**all** x", false, "@**topic** x")]
    [InlineData("@**topic** @**all** x", true, "@**all** x")]
    [InlineData("@_**all** x", false, "@_**all** x")]       // 静默提及不通知
    [InlineData("@**all|5** x", false, "@**all|5** x")]     // 名叫 all 的用户
    [InlineData("mail@**all**", false, "mail@**all**")]
    public void RemoveWildcards(string markdown, bool topic, string expected) =>
        Assert.Equal(expected, MentionText.RemoveWildcards(markdown, topic));
}
