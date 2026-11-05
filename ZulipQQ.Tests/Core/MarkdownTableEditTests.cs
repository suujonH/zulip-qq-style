using ZulipQQ.Core;

namespace ZulipQQ.Tests.Core;

public sealed class MarkdownTableEditTests
{
    private sealed class NullResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }

    private const string Sample = "| a | b |\n| --- | --- |\n| 1 | 2 |\n| 3 | 4 |";

    // 把光标放在 marker 之后操作，返回修改后的文字，光标用 « 标出；没有修改返回 null
    private static string? Run(string text, string marker, Func<MarkdownModel, string, MarkdownTable, int, MarkdownEdit?> edit)
    {
        var model = ZulipMarkdown.Parse(text, new NullResolver());
        var table = Assert.Single(MarkdownTables.Find(model));
        var caret = text.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
        if (edit(model, text, table, caret) is not { } change)
            return null;
        return text.Remove(change.Start, change.Length).Insert(change.Start, change.Replacement).Insert(change.SelectionStart, "«");
    }

    [Theory]
    [InlineData("a", 0, 0)]
    [InlineData("b", 0, 1)]
    [InlineData("1", 1, 0)]
    [InlineData("4", 2, 1)]
    public void CellAt(string marker, int row, int column)
    {
        var model = ZulipMarkdown.Parse(Sample, new NullResolver());
        var table = Assert.Single(MarkdownTables.Find(model));
        Assert.Equal((row, column), MarkdownTableEdit.CellAt(model, Sample, table, Sample.IndexOf(marker, StringComparison.Ordinal) + 1));
    }

    [Fact]
    public void InsertRow_Below() => Assert.Equal(
        "| a | b |\n| --- | --- |\n| 1 | 2 |\n|  | «|\n| 3 | 4 |".Replace("|  | «|", "|  | « |", StringComparison.Ordinal),
        Run(Sample, "2", (m, t, table, c) => MarkdownTableEdit.InsertRow(m, t, table, c, below: true)));

    [Fact]
    public void InsertColumn_Right() => Assert.Equal(
        "| a | « | b |\n| --- | --- | --- |\n| 1 |  | 2 |\n| 3 |  | 4 |",
        Run(Sample, "a", (m, t, table, c) => MarkdownTableEdit.InsertColumn(m, t, table, c, right: true)));

    [Fact]
    public void MoveRow_FollowsCaretAndStopsAtEdge()
    {
        Assert.Equal("| a | b |\n| --- | --- |\n| 3 | 4« |\n| 1 | 2 |",
            Run(Sample, "4", (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, -1)));
        Assert.Null(Run(Sample, "a", (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, -1)));
    }

    [Fact]
    public void MoveColumn_MovesAlignmentToo() => Assert.Equal(
        "| b« | a |\n| :-: | --- |\n| 2 | 1 |",
        Run("| a | b |\n| --- | :---: |\n| 1 | 2 |", "b", (m, t, table, c) => MarkdownTableEdit.MoveColumn(m, t, table, c, -1)));

    [Fact]
    public void Resize_ShrinkDiscardsContent() => Assert.Equal(
        "| a« | b |\n| --- | --- |\n| 1 | 2 |",
        Run("| a | b | c |\n| --- | --- | --- |\n| 1 | 2 | 3 |\n| 4 | 5 | 6 |", "a",
            (m, t, table, c) => MarkdownTableEdit.Resize(m, t, table, c, 2, 2)));

    [Fact]
    public void Resize_GrowAddsEmptyCells() => Assert.Equal(
        "| a« | b |  |\n| --- | --- | --- |\n| 1 | 2 |  |\n| 3 | 4 |  |\n|  |  |  |",
        Run(Sample, "a", (m, t, table, c) => MarkdownTableEdit.Resize(m, t, table, c, 3, 4)));

    [Fact]
    public void Resize_KeepsAtLeastTwoColumns() => Assert.Equal(
        "| a« | b |\n| --- | --- |",
        Run(Sample, "a", (m, t, table, c) => MarkdownTableEdit.Resize(m, t, table, c, 1, 1)));

    [Theory]
    [InlineData(TableAlign.Center, "| --- | :-: |")]
    [InlineData(TableAlign.Right, "| --- | --: |")]
    public void SetAlign(TableAlign align, string separator) => Assert.Equal(
        $"| a | b« |\n{separator}\n| 1 | 2 |",
        Run("| a | b |\n| --- | --- |\n| 1 | 2 |", "b", (m, t, table, c) => MarkdownTableEdit.SetAlign(m, t, table, c, align)));

    [Fact]
    public void SetAlign_SameIsNoChange() =>
        Assert.Null(Run(Sample, "b", (m, t, table, c) => MarkdownTableEdit.SetAlign(m, t, table, c, TableAlign.Left)));

    [Fact]
    public void DeleteRow_HeaderPromotesNext() => Assert.Equal(
        "| 1« | 2 |\n| --- | --- |\n| 3 | 4 |",
        Run(Sample, "a", MarkdownTableEdit.DeleteRow));

    [Fact]
    public void DeleteRow_LastOneRemovesTable() =>
        Assert.Equal("前\n«后", Run("前\n| a | b |\n| --- | --- |\n后", "a", MarkdownTableEdit.DeleteRow));

    [Fact]
    public void DeleteColumn_StopsAtTwoColumns()
    {
        Assert.Null(Run(Sample, "a", MarkdownTableEdit.DeleteColumn));
        Assert.Equal("| a | c« |\n| --- | --- |",
            Run("| a | b | c |\n| --- | --- | --- |", "b", MarkdownTableEdit.DeleteColumn));
    }

    [Fact]
    public void Format_PadsByDisplayWidth() => Assert.Equal(
        "| 标题« | b   |\n| ---- | --- |\n| 1    | 2   |",
        Run("| 标题 | b |\n| --- | --- |\n| 1 | 2 |", "题", MarkdownTableEdit.Format));

    [Fact]
    public void Empty_ClampsToMinimum() =>
        Assert.Equal("|  |  |\n| --- | --- |", MarkdownTableEdit.Empty(1, 0));

    [Fact]
    public void FromCells_Compact() =>
        Assert.Equal("| a | b |\n| --- | --: |\n| 1 |  |",
            MarkdownTableEdit.FromCells([["a", "b"], ["1"]], [TableAlign.Left, TableAlign.Right], padded: false));

    [Fact]
    public void FromCells_Padded_AlignsColumnsCountingCjkAsTwo() =>
        Assert.Equal("| 姓名      | 备注     |\n| --------- | :------: |\n| Alexander | 你好世界 |",
            MarkdownTableEdit.FromCells([["姓名", "备注"], ["Alexander", "你好世界"]], [TableAlign.Left, TableAlign.Center], padded: true));

    [Fact]
    public void FromCells_EscapesPipesAndLineBreaks() =>
        Assert.Equal(@"| a\|b | c<br />d |" + "\n| --- | --- |\n" + @"| e\|f |  |",
            MarkdownTableEdit.FromCells([["a|b", "c\nd"], [@"e\|f", ""]], [], padded: false));
}
