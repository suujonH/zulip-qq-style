using ZulipQQ.ViewModels;

namespace ZulipQQ.Tests.ViewModels;

public class ComposerQuoteTests
{
    [Fact]
    public void QuoteMarkdown_MatchesWebApp() =>
        Assert.Equal("@_**张三|5** [发言道](https://z.test/#narrow/dm/1,5-dm/near/9)：\n```quote\n你好\n```",
            ComposerViewModel.QuoteMarkdown("张三", 5, "https://z.test/#narrow/dm/1,5-dm/near/9", "你好"));

    [Fact]
    public void QuoteMarkdown_LengthensFenceOverInnerFence() =>
        Assert.Equal("@_**A|1** [发言道](l)：\n````quote\n```py\nx\n```\n````",
            ComposerViewModel.QuoteMarkdown("A", 1, "l", "```py\nx\n```"));
}
