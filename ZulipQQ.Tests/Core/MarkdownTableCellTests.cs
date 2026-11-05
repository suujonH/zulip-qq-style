using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

/// <summary>输入框表格的单元格：编辑时的文字与原文的转换、写回文档、列宽。</summary>
public class MarkdownTableCellTests
{
    private const string Table = "| a | b |\n| --- | --- |\n| c | d |";

    private sealed class NullResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }

    private static (MarkdownModel Model, MarkdownTable Table) Parse(string text)
    {
        var model = ZulipMarkdown.Parse(text, new NullResolver());
        return (model, Assert.Single(MarkdownTables.Find(model)));
    }

    private static string Apply(string text, MarkdownEdit edit) =>
        text[..edit.Start] + edit.Replacement + text[(edit.Start + edit.Length)..];

    [Theory]
    [InlineData("a\nb", "a<br />b")]
    [InlineData("c|d", "c\\|d")]
    [InlineData("plain", "plain")]
    public void Encode_and_decode_are_inverse(string value, string source)
    {
        Assert.Equal(source, MarkdownTableEdit.EncodeCell(value));
        Assert.Equal(value, MarkdownTableEdit.DecodeCell(source));
    }

    [Fact]
    public void Escaped_pipe_does_not_split_cells()
    {
        var text = "| c\\|d | e |";
        Assert.Equal(new[] { 0, 7, 11 }, ZulipMarkdown.TablePipes(text, 0, text.Length));
    }

    [Fact]
    public void SetCell_changes_only_that_cell()
    {
        var (model, table) = Parse(Table);
        var edit = MarkdownTableEdit.SetCell(model, Table, table, 1, 1, "x\ny|z");
        Assert.Equal("| a | b |\n| --- | --- |\n| c | x<br />y\\|z |", Apply(Table, edit));
    }

    [Fact]
    public void SetCell_on_a_short_row_rewrites_the_table()
    {
        const string text = "| a | b |\n| --- | --- |\n| c |";
        var (model, table) = Parse(text);
        var edit = MarkdownTableEdit.SetCell(model, text, table, 1, 1, "x");
        Assert.Equal("| a | b |\n| --- | --- |\n| c | x |", Apply(text, edit));
    }

    [Fact]
    public void CellValues_decode_and_fill_missing_cells()
    {
        const string text = "| a<br />b | c\\|d |\n| --- | --- |\n| e |";
        var (model, table) = Parse(text);
        var values = MarkdownTableEdit.CellValues(model, text, table);
        Assert.Equal(new[] { "a\nb", "c|d" }, values[0]);
        Assert.Equal(new[] { "e", "" }, values[1]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    public void CaretOf_is_the_inverse_of_CellAt(int row, int column)
    {
        var (model, table) = Parse(Table);
        var caret = MarkdownTableEdit.CaretOf(model, Table, table, row, column);
        Assert.Equal((row, column), MarkdownTableEdit.CellAt(model, Table, table, caret));
    }

    [Fact]
    public void Columns_share_the_width_equally_when_everything_fits()
    {
        Assert.Equal(new[] { 100.0, 100.0, 100.0 }, MarkdownTables.FitColumns([20, 50, 100], 300));
    }

    [Fact]
    public void Wide_column_takes_what_it_needs_and_the_others_share_the_rest()
    {
        Assert.Equal(new[] { 200.0, 50.0, 50.0 }, MarkdownTables.FitColumns([200, 20, 20], 300));
    }

    [Fact]
    public void Columns_keep_their_required_width_when_the_table_overflows()
    {
        Assert.Equal(new[] { 300.0, 200.0 }, MarkdownTables.FitColumns([300, 200], 300));
    }

    [Fact]
    public void Columns_shrink_towards_their_minimum_so_text_wraps()
    {
        Assert.Equal(new[] { 200.0, 100.0 }, MarkdownTables.FitColumns([300, 100], 300, [100, 100]));
    }

    [Fact]
    public void Columns_stop_at_their_minimum_and_the_table_scrolls()
    {
        Assert.Equal(new[] { 250.0, 100.0 }, MarkdownTables.FitColumns([400, 100], 300, [250, 100]));
    }

    // 每个字符宽 1
    private static double Width(string text) => text.Length;

    private static string[] Wrap(string line, double width)
    {
        var parts = new List<string>();
        var start = 0;
        foreach (var b in MarkdownTableWrap.Choose(line, MarkdownTableWrap.Candidates(line), Width, width))
        {
            parts.Add(line[start..b.TextEnd]);
            start = b.NextStart;
        }
        parts.Add(line[start..]);
        return parts.ToArray();
    }

    [Theory]
    [InlineData("aaa bbb", 4, new[] { "aaa", "bbb" })] // 空格留在上一行，不显示
    [InlineData("aaa　bbb", 4, new[] { "aaa", "bbb" })] // 全角空格
    [InlineData("aaa,bbb", 4, new[] { "aaa,", "bbb" })] // 符号留在上一行
    [InlineData("aa，bb。cc", 3, new[] { "aa，", "bb。", "cc" })]
    [InlineData("aaa(bbb)", 4, new[] { "aaa", "(bbb)" })] // 左括号到下一行
    [InlineData("aaa?!bbb", 5, new[] { "aaa?!", "bbb" })] // 连续的符号不断开
    [InlineData("1.5.5.5", 3, new[] { "1.5.5.5" })] // 半角句号可能是小数点
    [InlineData("中文没有断点", 3, new[] { "中文没有断点" })] // 太长时横向滚动，不在文字中间断开
    [InlineData("aaa bbb", 7, new[] { "aaa bbb" })] // 放得下时不断
    public void Wraps_only_at_allowed_places(string line, double width, string[] expected)
    {
        Assert.Equal(expected, Wrap(line, width));
    }

    [Fact]
    public void Protected_text_does_not_break()
    {
        Assert.Empty(MarkdownTableWrap.Candidates("a-b c", i => i < 5));
    }

    [Fact]
    public void Widest_of_all_candidates_is_the_longest_unbreakable_run()
    {
        const string line = "aa bbbb,c";
        Assert.Equal(5, MarkdownTableWrap.Widest(line, MarkdownTableWrap.Candidates(line), Width));
    }
}
