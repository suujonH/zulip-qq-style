using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class InlineMarkdownTests
{
    // 段落 “普通粗体@甲 链接😄”：普通 0-2，粗体 2-4，提及 4-6，空格 6-7，链接 7-9，表情 9-10
    private const string Text = "普通粗体@甲 链接￼";

    private static readonly InlinePiece[] Pieces =
    [
        new(0, 2, null),
        new(2, 4, null, Bold: true),
        new(4, 6, "@**甲|1**"),
        new(6, 7, null),
        new(7, 9, null, Href: "https://a.com"),
        new(9, 10, ":smile:"),
    ];

    [Theory]
    [InlineData(0, 10, "普通**粗体**@**甲|1** [链接](https://a.com):smile:")]
    [InlineData(1, 3, "通**粗**")]
    [InlineData(5, 6, "@**甲|1**")]
    [InlineData(8, 9, "[接](https://a.com)")]
    [InlineData(0, 0, "")]
    public void FromPieces_Selection(int start, int end, string expected) =>
        Assert.Equal(expected, InlineMarkdown.FromPieces(Text, Pieces, start, end));

    [Fact]
    public void FromPieces_MergesSameStyleAndKeepsSpacesOutside()
    {
        const string text = "a b c";
        InlinePiece[] pieces = [new(0, 2, null, Bold: true), new(2, 4, null, Bold: true), new(4, 5, null)];
        Assert.Equal("**a b** c", InlineMarkdown.FromPieces(text, pieces, 0, 5));
    }

    [Fact]
    public void FromPieces_NestedStylesAndCode()
    {
        const string text = "xy`z";
        InlinePiece[] pieces = [new(0, 2, null, Bold: true, Italic: true), new(2, 4, null, Code: true)];
        Assert.Equal("***xy***`` `z ``", InlineMarkdown.FromPieces(text, pieces, 0, 4));
    }

    [Fact]
    public void FromPieces_TextOutsidePiecesIsRaw()
    {
        const string text = "a\nb";
        InlinePiece[] pieces = [new(0, 1, null, Strike: true), new(2, 3, null, Strike: true)];
        Assert.Equal("~~a~~\n~~b~~", InlineMarkdown.FromPieces(text, pieces, 0, 3));
    }

    [Fact]
    public void FromPieces_LinkWithSameTextIsBareUrl()
    {
        const string text = "https://a.com";
        InlinePiece[] pieces = [new(0, text.Length, null, Href: text)];
        Assert.Equal(text, InlineMarkdown.FromPieces(text, pieces, 0, text.Length));
    }

    [Theory]
    [InlineData(0, 10, "```py\nprint(1)\nx\n```")]
    [InlineData(0, 5, "`print`")]
    [InlineData(6, 10, "```py\n1)\nx\n```")]
    public void FromCode(int start, int end, string expected) =>
        Assert.Equal(expected, InlineMarkdown.FromCode("print(1)\nx", "py", start, end));
}
