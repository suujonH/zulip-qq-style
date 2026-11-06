using System;
using System.Linq;
using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class ZulipMarkdownTests
{
    private sealed class FakeResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => id is null ? name == "甲" : id == 1;
        public bool HasGroup(string name) => name == "开发";
        public bool HasChannel(string name) => name == "综合";
        public bool HasEmoji(string name) => name == "smile";
    }

    private static MarkdownModel Parse(string text) => ZulipMarkdown.Parse(text, new FakeResolver());

    private static string Kinds(string text) =>
        string.Join(",", Parse(text).Spans.Select(s => $"{s.Kind}:{text[s.Start..s.End]}"));

    private static string Content(string text, MdSpan s) => text[s.ContentStart..s.ContentEnd];

    [Theory]
    [InlineData("**粗**", "Strong:**粗**")]
    [InlineData("*斜*", "Em:*斜*")]
    [InlineData("***粗斜***", "StrongEm:***粗斜***")]
    [InlineData("~~删~~", "Del:~~删~~")]
    [InlineData("`代码`", "Code:`代码`")]
    [InlineData("_x_", "")]
    [InlineData("__x__", "")]
    [InlineData("**a\nb**", "")]
    [InlineData("const char *x = (char *)y", "")]
    [InlineData("`a **b** c`", "Code:`a **b** c`")]
    [InlineData("`@**甲|1**`", "Code:`@**甲|1**`")]
    [InlineData("```\n@**甲|1**\n```", "")]
    [InlineData("[a **b**](https://a.com)", "Link:[a **b**](https://a.com)")]
    public void Inline_Emphasis(string text, string expected) => Assert.Equal(expected, Kinds(text));

    [Fact]
    public void Inline_EmContainsStrong()
    {
        const string text = "*a **b** c*";
        var spans = Parse(text).Spans;
        Assert.Equal(2, spans.Count);
        Assert.Equal(MdSpanKind.Em, spans[0].Kind);
        Assert.Equal((0, 11), (spans[0].Start, spans[0].End));
        Assert.Equal(MdSpanKind.Strong, spans[1].Kind);
        Assert.Equal("b", Content(text, spans[1]));
    }

    [Fact]
    public void Inline_CodeContent()
    {
        const string text = "x ``a`b`` y";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal("a`b", Content(text, span));
    }

    [Theory]
    [InlineData("@**甲|1**", MdSpanKind.UserMention, "@甲")]
    [InlineData("@**甲**", MdSpanKind.UserMention, "@甲")]
    [InlineData("@_**甲|1**", MdSpanKind.UserMention, "甲")]
    [InlineData("@**all**", MdSpanKind.UserMention, "@全体成员")]
    [InlineData("@**topic**", MdSpanKind.UserMention, "@话题参与者")]
    [InlineData("@*开发*", MdSpanKind.GroupMention, "@开发")]
    [InlineData("#**综合**", MdSpanKind.ChannelLink, "#综合")]
    [InlineData("#**综合>话题**", MdSpanKind.ChannelLink, "#综合 > 话题")]
    [InlineData("#**综合>话题@12**", MdSpanKind.ChannelLink, "#综合 > 话题")]
    [InlineData(":smile:", MdSpanKind.Emoji, "smile")]
    [InlineData("<time:2026-10-24T15:00:00+08:00>", MdSpanKind.Time, "2026-10-24T15:00:00+08:00")]
    public void Inline_Atomic(string text, MdSpanKind kind, string data)
    {
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(kind, span.Kind);
        Assert.Equal((0, text.Length), (span.Start, span.End));
        Assert.Equal(data, span.Data);
        Assert.True(ZulipMarkdown.IsAtomic(kind));
    }

    [Fact]
    public void Inline_UnknownUserIsStrong()
    {
        const string text = "@**乙**";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.Strong, span.Kind);
        Assert.Equal("乙", Content(text, span));
    }

    [Theory]
    [InlineData("a@**甲|1**")]
    [InlineData("#**未知**")]
    [InlineData("$$_$$")]
    [InlineData("<time:abc>")]
    [InlineData(":unknown:")]
    public void Inline_NotAtomic(string text) =>
        Assert.DoesNotContain(Parse(text).Spans, s => ZulipMarkdown.IsAtomic(s.Kind));

    [Fact]
    public void Inline_Math()
    {
        const string text = "x $$x^2$$ y";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.Math, span.Kind);
        Assert.Equal("x^2", Content(text, span));
    }

    [Fact]
    public void Inline_Link()
    {
        const string text = "看 [文字](https://a.com/x) 吧";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.Link, span.Kind);
        Assert.Equal("[文字](https://a.com/x)", text[span.Start..span.End]);
        Assert.Equal("文字", Content(text, span));
        Assert.Equal("https://a.com/x", span.Data);
    }

    [Fact]
    public void Inline_LinkNestedBrackets()
    {
        const string text = "[a [b] c](u)";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal("a [b] c", Content(text, span));
        Assert.Equal("u", span.Data);
    }

    [Fact]
    public void Inline_LinkEmptyLabel()
    {
        const string text = "[](https://a.com)";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.Link, span.Kind);
        Assert.Equal(span.ContentStart, span.ContentEnd);
        Assert.Equal("https://a.com", span.Data);
    }

    [Theory]
    [InlineData("![图](/user_uploads/1/ab/x.png)")]
    [InlineData("[图](/user_uploads/1/ab/x.png)")]
    public void Inline_UploadImage(string text)
    {
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.UploadImage, span.Kind);
        Assert.Equal((0, text.Length), (span.Start, span.End));
        Assert.Equal("/user_uploads/1/ab/x.png", span.Data);
    }

    [Theory]
    [InlineData("[a.zip](/user_uploads/1/ab/a.zip)", "/user_uploads/1/ab/a.zip")]
    [InlineData("[a.mp4](/user_uploads/1/ab/a.mp4)", "/user_uploads/1/ab/a.mp4")]
    public void Inline_UploadFile(string text, string address)
    {
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.UploadFile, span.Kind);
        Assert.Equal((0, text.Length), (span.Start, span.End));
        Assert.Equal(address, span.Data);
        Assert.Equal(text[1..text.IndexOf(']')], text[span.ContentStart..span.ContentEnd]);
    }

    [Fact]
    public void Inline_UploadFileWithoutLabelIsLink() =>
        Assert.Equal(MdSpanKind.Link, Assert.Single(Parse("[](/user_uploads/1/ab/a.zip)").Spans).Kind);

    [Fact]
    public void Tables_FindsRowsAfterHeaderAndSeparator()
    {
        var table = Assert.Single(MarkdownTables.Find(Parse("前\n\n| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |\n\n后")));
        Assert.Equal(new MarkdownTable(2, 3, 5), table);
    }

    [Theory]
    [InlineData("| a |")]
    [InlineData("> | a | b |\n> | --- | --- |\n> | 1 | 2 |")]
    public void Tables_IgnoresIncompleteOrQuoted(string text) =>
        Assert.Empty(MarkdownTables.Find(Parse(text)));

    [Fact]
    public void Tables_PipesInCodeAreNotSeparators()
    {
        const string text = "| `a|b` | c |";
        Assert.Equal([0, 8, 12], ZulipMarkdown.TablePipes(text, 0, text.Length));
    }

    [Fact]
    public void Inline_UploadPlaceholder()
    {
        var span = Assert.Single(Parse("x [Uploading 截图.png…]() y").Spans);
        Assert.Equal(MdSpanKind.UploadPlaceholder, span.Kind);
        Assert.Equal("截图.png", span.Data);
    }

    [Theory]
    [InlineData("看 https://a.com/x).", "https://a.com/x")]
    [InlineData("(https://a.com/x(1))", "https://a.com/x(1)")]
    [InlineData("中文https://a.com/x 后", "https://a.com/x")]
    public void Inline_Url(string text, string url)
    {
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal(MdSpanKind.Url, span.Kind);
        Assert.Equal(url, span.Data);
        Assert.Equal(url, text[span.Start..span.End]);
    }

    [Fact]
    public void Inline_UrlInsideLinkIsNotUrl() =>
        Assert.Equal(MdSpanKind.Link, Assert.Single(Parse("[https://a.com](https://a.com)").Spans).Kind);

    [Fact]
    public void Inline_SecondLineOffsets()
    {
        const string text = "第一行\n**粗**";
        var span = Assert.Single(Parse(text).Spans);
        Assert.Equal((4, 9), (span.Start, span.End));
        Assert.Equal("粗", Content(text, span));
    }

    [Fact]
    public void MentionSpans_OnlyMentions()
    {
        const string text = "@**甲|1** **x** @*开发*";
        var spans = ZulipMarkdown.MentionSpans(Parse(text), text);
        Assert.Equal(["@**甲|1**", "@*开发*"], spans.Select(s => s.Markdown));
    }

    // ───────────── 块 ─────────────

    [Fact]
    public void Block_Heading()
    {
        var line = Parse("## 标题").Lines[0];
        Assert.Equal(MdLineKind.Heading, line.Kind);
        Assert.Equal(2, line.HeadingLevel);
        Assert.Equal(new MdPrefix(MdPrefixKind.Heading, 0, 3), Assert.Single(line.Prefixes));
        Assert.Equal(3, line.ContentStart);
        Assert.Equal(MdLineKind.Paragraph, Parse("#标题").Lines[0].Kind);
    }

    [Fact]
    public void Block_Quote()
    {
        var line = Parse("> 引用").Lines[0];
        Assert.Equal(MdLineKind.Paragraph, line.Kind);
        Assert.Equal(1, line.QuoteDepth);
        Assert.Equal(2, line.ContentStart);

        var two = Parse("> > 两层").Lines[0];
        Assert.Equal(2, two.QuoteDepth);
        Assert.Equal(2, two.Prefixes.Count);
        Assert.Equal(4, two.ContentStart);

        var bare = Parse(">").Lines[0];
        Assert.Equal(0, bare.QuoteDepth);
        Assert.Empty(bare.Prefixes);
    }

    [Theory]
    [InlineData("- a", MdPrefixKind.Bullet, 0, 2)]
    [InlineData("* a", MdPrefixKind.Bullet, 0, 2)]
    [InlineData("  1. a", MdPrefixKind.Ordered, 2, 5)]
    public void Block_List(string text, MdPrefixKind kind, int start, int end)
    {
        var line = Parse(text).Lines[0];
        Assert.Equal(MdLineKind.ListItem, line.Kind);
        Assert.Equal(new MdPrefix(kind, start, end), Assert.Single(line.Prefixes));
    }

    [Theory]
    [InlineData("---")]
    [InlineData("* * *")]
    public void Block_Rule(string text) => Assert.Equal(MdLineKind.Rule, Parse(text).Lines[0].Kind);

    [Fact]
    public void Block_CodeFence()
    {
        var model = Parse("```python\n**x**\n```\n**y**");
        Assert.Equal([MdLineKind.FenceOpen, MdLineKind.FenceBody, MdLineKind.FenceClose, MdLineKind.Paragraph],
            model.Lines.Select(l => l.Kind));
        Assert.Equal("python", model.Lines[0].FenceInfo);
        Assert.Equal(MdFenceKind.Code, model.Lines[1].Fence);
        // 只有围栏外的 **y** 是加粗
        Assert.Equal(20, Assert.Single(model.Spans).Start);
    }

    [Fact]
    public void Block_QuoteFence()
    {
        var model = Parse("```quote\n**x**\n```");
        Assert.Equal(MdFenceKind.Quote, model.Lines[0].Fence);
        Assert.Equal(MdLineKind.Paragraph, model.Lines[1].Kind);
        Assert.Equal(1, model.Lines[1].QuoteDepth);
        Assert.Equal(MdFenceKind.Quote, model.Lines[1].Fence);
        Assert.Equal(MdSpanKind.Strong, Assert.Single(model.Spans).Kind);
        Assert.Equal(MdLineKind.FenceClose, model.Lines[2].Kind);
    }

    [Fact]
    public void Block_SpoilerFence()
    {
        var line = Parse("```spoiler 标题\n内容\n```").Lines[0];
        Assert.Equal(MdFenceKind.Spoiler, line.Fence);
        Assert.Equal("标题", line.FenceInfo);
    }

    [Fact]
    public void Block_UnclosedFence()
    {
        var model = Parse("```\na\nb");
        Assert.Equal([MdLineKind.FenceOpen, MdLineKind.FenceBody, MdLineKind.FenceBody], model.Lines.Select(l => l.Kind));
    }

    [Fact]
    public void Block_IndentedCode()
    {
        Assert.Equal(MdLineKind.IndentedCode, Parse("a\n\n    code").Lines[2].Kind);
        Assert.Equal(MdLineKind.IndentedCode, Parse("    code").Lines[0].Kind);
        Assert.NotEqual(MdLineKind.IndentedCode, Parse("- a\n\n    b").Lines[2].Kind);
        Assert.NotEqual(MdLineKind.IndentedCode, Parse("a\n    b").Lines[1].Kind);
    }

    [Fact]
    public void Block_Table()
    {
        var model = Parse("a | `x|y`\n--- | ---\n**1** | 2\n\nc");
        Assert.Equal([MdLineKind.TableRow, MdLineKind.TableSeparator, MdLineKind.TableRow, MdLineKind.Blank, MdLineKind.Paragraph],
            model.Lines.Select(l => l.Kind));
        Assert.Contains(model.Spans, s => s.Kind == MdSpanKind.Code);
        Assert.Contains(model.Spans, s => s.Kind == MdSpanKind.Strong);
    }

    [Fact]
    public void LineIndexAt()
    {
        var model = Parse("ab\ncd\n");
        Assert.Equal(0, model.LineIndexAt(0));
        Assert.Equal(0, model.LineIndexAt(2));
        Assert.Equal(1, model.LineIndexAt(3));
        Assert.Equal(2, model.LineIndexAt(6));
        Assert.Equal(-1, MarkdownModel.Empty.LineIndexAt(0));
    }

    private static string Preview(string text) =>
        ZulipMarkdown.ToPreview(text, new FakeResolver(), name => name == "smile" ? "😄" : null);

    [Theory]
    [InlineData("你好\n\n世界", "你好 世界")]
    [InlineData("**粗** *斜* ~~删~~ `代码`", "粗 斜 删 代码")]
    [InlineData("*a **b** c*", "a b c")]
    [InlineData("hi @**甲|1** and @_**甲|1**", "hi @甲 and 甲")]
    [InlineData("@*开发* 看看", "@开发 看看")]
    [InlineData("@**乙|2**", "@乙|2")]
    [InlineData("#**综合>午饭** 去", "#综合 > 午饭 去")]
    [InlineData("笑:smile:", "笑 😄")]
    [InlineData("[文档](https://a.com) [](https://b.com)", "文档 https://b.com")]
    [InlineData("看 ![](https://a.com/x.png)", "看 [图片]")]
    [InlineData("[a.png](/user_uploads/1/ab/a.png)", "[图片]")]
    [InlineData("[a.mp4](/user_uploads/1/ab/a.mp4)", "[视频]")]
    [InlineData("[报告.pdf](/user_uploads/1/ab/r.pdf)", "[文件]报告.pdf")]
    [InlineData("[Uploading a.png…]() 好", "好")]
    [InlineData("$$x^2$$ 是", "[公式] 是")]
    [InlineData("- 一\n- 二\n1. 三", "• 一 • 二 1. 三")]
    [InlineData("- @**甲|1** 好", "• @甲 好")]
    [InlineData("## 标题\n正文", "标题 正文")]
    [InlineData("> 引用\n回复", "回复")]
    [InlineData("```quote\n引用\n```\n回复", "回复")]
    [InlineData("前\n```py\nprint(1)\n```\n后", "前 [代码] 后")]
    [InlineData("```math\nx\n```", "[公式]")]
    [InlineData("```spoiler 标题\n```py\nx\n```\n内容\n```\n后", "[折叠] 后")]
    [InlineData("前\n\n    a\n    b\n\n后", "前 [代码] 后")]
    [InlineData("a | b\n--- | ---\n1 | 2", "a b 1 2")]
    [InlineData("---\n文字", "文字")]
    public void Preview_MatchesMessagePreview(string text, string expected) => Assert.Equal(expected, Preview(text));

    private static string Plain(string text) =>
        ZulipMarkdown.ToPlainText(text, new FakeResolver(), name => name == "smile" ? "😄" : null);

    [Theory]
    [InlineData("**粗** *斜*\n\n第二段", "粗 斜\n\n第二段")]
    [InlineData("hi @**甲|1** #**综合** :smile:", "hi @甲 #综合 😄")]
    [InlineData("[文档](https://a.com) [](https://b.com) https://c.com", "文档 (https://a.com) https://b.com https://c.com")]
    [InlineData("[报告.pdf](/user_uploads/1/ab/r.pdf) ![](https://a.com/x.png)", "报告.pdf [图片]")]
    [InlineData("- 一\n  - 二\n1. 三", "• 一\n  • 二\n1. 三")]
    [InlineData("## 标题", "标题")]
    [InlineData("> 引用 **粗**\n回复", "> 引用 粗\n回复")]
    [InlineData("```quote\n引用\n```", "> 引用")]
    [InlineData("```py\nx = **1**\n```", "x = **1**")]
    [InlineData("```spoiler 标题\n内容\n```", "标题\n内容")]
    [InlineData("    code", "code")]
    [InlineData("$$x^2$$", "$$x^2$$")]
    public void PlainText_IsDisplayedText(string text, string expected) => Assert.Equal(expected, Plain(text));
}
