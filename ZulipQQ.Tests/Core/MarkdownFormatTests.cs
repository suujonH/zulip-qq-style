using System;
using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class MarkdownFormatTests
{
    // 选中范围用 « » 标出（没有 » 时 « 为光标）；返回修改后的文字，同样用 « » 标出选中范围
    private static string Apply(string marked, MarkdownFormatKind kind)
    {
        var (text, s, e) = Unmark(marked);
        return Mark(text, MarkdownFormat.Apply(text, s, e, kind));
    }

    // 带解析结果：光标在格式中时取消该格式
    private static string ApplyAt(string marked, MarkdownFormatKind kind)
    {
        var (text, s, e) = Unmark(marked);
        return Mark(text, MarkdownFormat.Apply(text, s, e, kind, ZulipMarkdown.Parse(text, new NullResolver())));
    }

    private sealed class NullResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }

    private static (string Text, int Start, int End) Unmark(string marked)
    {
        var s = marked.IndexOf('«');
        var e = marked.IndexOf('»');
        if (e < 0)
            return (marked.Remove(s, 1), s, s);
        return (marked.Remove(e, 1).Remove(s, 1), s, e - 1);
    }

    private static string Mark(string text, MarkdownEdit edit)
    {
        var result = text.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.Replacement);
        if (edit.SelectionStart == edit.SelectionEnd)
            return result.Insert(edit.SelectionStart, "«");
        return result.Insert(edit.SelectionEnd, "»").Insert(edit.SelectionStart, "«");
    }

    [Theory]
    [InlineData("a «b» c", MarkdownFormatKind.Bold, "a **«b»** c")]
    [InlineData("a «b» c", MarkdownFormatKind.Italic, "a *«b»* c")]
    [InlineData("a «b» c", MarkdownFormatKind.Strikethrough, "a ~~«b»~~ c")]
    [InlineData("a «b» c", MarkdownFormatKind.Code, "a `«b»` c")]
    [InlineData("a «b» c", MarkdownFormatKind.Math, "a $$«b»$$ c")]
    [InlineData("a« b »c", MarkdownFormatKind.Bold, "a **«b»** c")]
    [InlineData("a «", MarkdownFormatKind.Bold, "a **«**")]
    [InlineData("a «", MarkdownFormatKind.Italic, "a *«*")]
    public void Wrap(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("a **«b»** c", MarkdownFormatKind.Bold, "a «b» c")]
    [InlineData("a *«b»* c", MarkdownFormatKind.Italic, "a «b» c")]
    [InlineData("a ~~«b»~~ c", MarkdownFormatKind.Strikethrough, "a «b» c")]
    [InlineData("a `«b»` c", MarkdownFormatKind.Code, "a «b» c")]
    [InlineData("a $$«b»$$ c", MarkdownFormatKind.Math, "a «b» c")]
    public void Unwrap_Outside(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("a «**b**» c", MarkdownFormatKind.Bold, "a «b» c")]
    [InlineData("a «*b*» c", MarkdownFormatKind.Italic, "a «b» c")]
    [InlineData("a «~~b~~» c", MarkdownFormatKind.Strikethrough, "a «b» c")]
    [InlineData("a «`b`» c", MarkdownFormatKind.Code, "a «b» c")]
    public void Unwrap_Inside(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("**«x»**", MarkdownFormatKind.Italic, "***«x»***")]
    [InlineData("*«x»*", MarkdownFormatKind.Bold, "***«x»***")]
    [InlineData("***«x»***", MarkdownFormatKind.Italic, "**«x»**")]
    [InlineData("***«x»***", MarkdownFormatKind.Bold, "*«x»*")]
    [InlineData("«**x**»", MarkdownFormatKind.Italic, "*«**x**»*")]
    public void StarCounting(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("看«文字»吧", "看[文字](«url»)吧")]
    [InlineData("«", "[](«url»)")]
    [InlineData("«[文字](url)»", "«文字»")]
    [InlineData("«[文字](https://a.com)»", "«文字 https://a.com»")]
    public void Link(string marked, string expected) => Assert.Equal(expected, Apply(marked, MarkdownFormatKind.Link));

    [Theory]
    [InlineData("«a\nb»", MarkdownFormatKind.BulletedList, "«- a\n- b»")]
    [InlineData("«a\n\nb»", MarkdownFormatKind.NumberedList, "«1. a\n\n2. b»")]
    [InlineData("«- a\n- b»", MarkdownFormatKind.BulletedList, "«a\nb»")]
    [InlineData("«1. a\n2. b»", MarkdownFormatKind.NumberedList, "«a\nb»")]
    [InlineData("«- a\n- b»", MarkdownFormatKind.NumberedList, "«1. a\n2. b»")]
    [InlineData("x\n  a«b\nc\n»", MarkdownFormatKind.BulletedList, "x\n«  - ab\n- c»\n")]
    public void List(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("x«a\nb»y", MarkdownFormatKind.Code, "x\n```«\na\nb\n```\ny")]
    [InlineData("«", MarkdownFormatKind.CodeBlock, "```«\n\n```")]
    [InlineData("x «a» y", MarkdownFormatKind.QuoteBlock, "x \n```quote\n«a»\n```\n y")]
    [InlineData("«a»", MarkdownFormatKind.Spoiler, "```spoiler «Header»\na\n```")]
    [InlineData("«a\nb»", MarkdownFormatKind.Math, "```math\n«a\nb»\n```")]
    [InlineData("«a\n```\nb»", MarkdownFormatKind.Code, "````«\na\n```\nb\n````")]
    [InlineData("«", MarkdownFormatKind.MathBlock, "```math\n«\n```")]
    public void Block(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("a «", MarkdownFormatKind.Code, "a `«`")]
    [InlineData("a «", MarkdownFormatKind.Math, "a $$«$$")]
    public void EmptyInline(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("a **b«c** d", MarkdownFormatKind.Bold, "a b«c d")]
    [InlineData("a *b«c* d", MarkdownFormatKind.Italic, "a b«c d")]
    [InlineData("***b«c***", MarkdownFormatKind.Italic, "**b«c**")]
    [InlineData("***b«c***", MarkdownFormatKind.Bold, "*b«c*")]
    [InlineData("a ~~b«c~~", MarkdownFormatKind.Strikethrough, "a b«c")]
    [InlineData("`co«de`", MarkdownFormatKind.Code, "co«de")]
    [InlineData("x [文«字](https://a.com) y", MarkdownFormatKind.Link, "x 文«字 https://a.com y")]
    [InlineData("x [文字](https://a«.com) y", MarkdownFormatKind.Link, "x 文字 https://a«.com y")]
    [InlineData("x [文«字](url) y", MarkdownFormatKind.Link, "x 文«字 y")]
    [InlineData("x [](https://a«.com) y", MarkdownFormatKind.Link, "x https://a«.com y")]
    [InlineData("x [文字](u«rl) y", MarkdownFormatKind.Link, "x 文字« y")]
    [InlineData("x [文字](«url») y", MarkdownFormatKind.Link, "x 文字« y")]
    [InlineData("x [文字](«https://a.com») y", MarkdownFormatKind.Link, "x 文字 https://a.com« y")]
    [InlineData("x [«文»字](https://a.com) y", MarkdownFormatKind.Link, "x 文«字 https://a.com y")]
    [InlineData("a **b** «c", MarkdownFormatKind.Bold, "a **b** **«c**")]
    [InlineData("hel«lo world", MarkdownFormatKind.Bold, "**hel«lo** world")]
    [InlineData("hel«lo world", MarkdownFormatKind.Italic, "*hel«lo* world")]
    [InlineData("你好«世界，再见", MarkdownFormatKind.Bold, "**你好«世界**，再见")]
    [InlineData("hel«lo", MarkdownFormatKind.Link, "[hello](«url»)")]
    [InlineData("hel«lo", MarkdownFormatKind.Code, "`hel«lo`")]
    [InlineData("a «, b", MarkdownFormatKind.Bold, "a **«**, b")]
    [InlineData("a «**b**", MarkdownFormatKind.Bold, "a **«****b**")]
    public void RemoveAtCaret(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, ApplyAt(marked, kind));

    [Theory]
    [InlineData("a«b", MarkdownFormatKind.Quote, "> a«b")]
    [InlineData("> a«b", MarkdownFormatKind.Quote, "a«b")]
    [InlineData(">«ab", MarkdownFormatKind.Quote, "«ab")]
    [InlineData("«a\n\nb»", MarkdownFormatKind.Quote, "«> a\n\n> b»")]
    [InlineData("«> a\n> b»", MarkdownFormatKind.Quote, "«a\nb»")]
    [InlineData("x\na«b", MarkdownFormatKind.BulletedList, "x\n- a«b")]
    [InlineData("- a«b", MarkdownFormatKind.BulletedList, "a«b")]
    [InlineData("1. a«b", MarkdownFormatKind.BulletedList, "- a«b")]
    [InlineData("«", MarkdownFormatKind.NumberedList, "1. «")]
    public void LinesAtCaret(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("ab«c", 2, "## ab«c")]
    [InlineData("## ab«c", 2, "ab«c")]
    [InlineData("# ab«c", 3, "### ab«c")]
    [InlineData("## a«b", 0, "a«b")]
    [InlineData("> a«b", 1, "> # a«b")]
    [InlineData("«a\n\n## b»", 1, "«# a\n\n# b»")]
    [InlineData("«", 1, "# «")]
    public void Heading(string marked, int level, string expected)
    {
        var (text, s, e) = Unmark(marked);
        Assert.Equal(expected, Mark(text, MarkdownFormat.SetHeading(text, s, e, level)));
    }

    [Theory]
    [InlineData("abc«", MarkdownFormatKind.Rule, "abc\n\n---\n«")]
    [InlineData("«", MarkdownFormatKind.Rule, "---\n«")]
    [InlineData("a\n«\nb", MarkdownFormatKind.Rule, "a\n\n---\n«b")]
    [InlineData("abc«", MarkdownFormatKind.Table, "abc\n\n| «标题» | 标题 |\n| --- | --- |\n| 内容 | 内容 |")]
    [InlineData("«x", MarkdownFormatKind.Table, "| «标题» | 标题 |\n| --- | --- |\n| 内容 | 内容 |\n\nx")]
    public void Insert(string marked, MarkdownFormatKind kind, string expected) => Assert.Equal(expected, Apply(marked, kind));

    [Theory]
    [InlineData("a **b«c**", MarkdownFormatKind.Bold, true)]
    [InlineData("a **bc**«", MarkdownFormatKind.Bold, false)]
    [InlineData("***b«***", MarkdownFormatKind.Italic, true)]
    [InlineData("[a«](u)", MarkdownFormatKind.Link, true)]
    [InlineData("> a«", MarkdownFormatKind.Quote, true)]
    [InlineData("- a«", MarkdownFormatKind.BulletedList, true)]
    [InlineData("- a«", MarkdownFormatKind.NumberedList, false)]
    [InlineData("2. a«", MarkdownFormatKind.NumberedList, true)]
    public void IsActive(string marked, MarkdownFormatKind kind, bool expected)
    {
        var (text, s, _) = Unmark(marked);
        Assert.Equal(expected, MarkdownFormat.IsActive(ZulipMarkdown.Parse(text, new NullResolver()), s, kind));
    }

    [Theory]
    [InlineData("### a«", 3)]
    [InlineData("a\n## b«", 2)]
    [InlineData("a«", 0)]
    public void HeadingLevelAt(string marked, int expected)
    {
        var (text, s, _) = Unmark(marked);
        Assert.Equal(expected, MarkdownFormat.HeadingLevelAt(ZulipMarkdown.Parse(text, new NullResolver()), s));
    }

    [Theory]
    [InlineData("在«", "在 <time:2026-10-24T15:00:00+08:00> «")]
    [InlineData("在 «", "在 <time:2026-10-24T15:00:00+08:00> «")]
    [InlineData("«旧»", "<time:2026-10-24T15:00:00+08:00> «")]
    public void InsertTime(string marked, string expected)
    {
        var (text, s, e) = Unmark(marked);
        var edit = MarkdownFormat.InsertTime(text, s, e, new DateTimeOffset(2026, 10, 24, 15, 0, 0, TimeSpan.FromHours(8)));
        Assert.Equal(expected, Mark(text, edit));
    }

    [Theory]
    [InlineData("你好", "你好")]
    [InlineData("```quote\n引用\n```", "```quote\n引用\n```\n")]
    [InlineData("第一行\n第二行\n", "第一行\n第二行\n")]
    [InlineData("", "")]
    public void WithTrailingLine(string content, string expected) => Assert.Equal(expected, MarkdownFormat.WithTrailingLine(content));

    // 与网页版 smart_insert_block 相同：前后补足两个换行，开头不补
    [Theory]
    [InlineData("«", "B\n\n")]
    [InlineData("前«", "\n\nB\n\n")]
    [InlineData("前\n«", "\nB\n\n")]
    [InlineData("前\n\n«\n后", "B\n")]
    [InlineData("«\n\n后", "B")]
    public void PadBlock(string marked, string expected)
    {
        var (text, s, e) = Unmark(marked);
        Assert.Equal(expected, MarkdownFormat.PadBlock(text, s, e, "B"));
    }

    [Theory]
    [InlineData("你好", "```")]
    [InlineData("a ``` b", "```")]
    [InlineData("```\nx\n```", "````")]
    [InlineData("   `````py\nx", "``````")]
    [InlineData("    ```\nx", "```")]
    public void UnusedFence(string content, string expected) => Assert.Equal(expected, MarkdownFormat.UnusedFence(content));
}
