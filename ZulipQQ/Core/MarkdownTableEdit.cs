using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZulipQQ.Core;

public enum TableAlign { Left, Center, Right }

/// <summary>
/// 表格的修改：解析成单元格，改完整个表格重新写出（一步撤销），光标放在目标单元格文字的末尾。
/// 行号 <c>row</c> 不算分隔行（0 为标题行），列号 <c>column</c> 从 0 起。
/// 操作不了时（已经在边上、最后的两列）返回 null，不修改。
/// </summary>
public static class MarkdownTableEdit
{
    /// <summary>Zulip 的表格至少两列（分隔行只有一列时不是表格，见 ZulipMarkdown 的 TableSeparatorPattern）。</summary>
    public const int MinColumns = 2;

    public const int MaxColumns = 20;
    public const int MinRows = 1;
    public const int MaxRows = 50;

    /// <summary>单元格中的换行（Markdown 表格的一行不能真的换行）。</summary>
    public const string LineBreak = "<br />";

    /// <summary>单元格的原文 → 编辑时的文字：<see cref="LineBreak"/> 是真的换行，\| 是 |。</summary>
    public static string DecodeCell(string source) => source.Replace(LineBreak, "\n").Replace("\\|", "|");

    /// <summary>编辑时的文字 → 单元格的原文（<see cref="DecodeCell"/> 的逆操作）。</summary>
    public static string EncodeCell(string value) => value.Replace("|", "\\|").Replace("\n", LineBreak);

    /// <summary>每个单元格编辑时的文字（行、列）；列数不够的行补空。</summary>
    public static IReadOnlyList<IReadOnlyList<string>> CellValues(MarkdownModel model, string text, MarkdownTable table) =>
        Parse(model, text, table).Rows.Select(cells => (IReadOnlyList<string>)cells.Select(DecodeCell).ToList()).ToList();

    /// <summary>放在这个单元格中的光标位置（<see cref="CellAt"/> 的逆操作）：编辑表格的操作用它指定单元格。</summary>
    public static int CaretOf(MarkdownModel model, string text, MarkdownTable table, int row, int column)
    {
        var line = model.Lines[row == 0 ? table.HeaderLine : table.SeparatorLine + row];
        var pipes = ZulipMarkdown.TablePipes(text, line.ContentStart, line.End);
        var hasLead = pipes.Count > 0 && !string.IsNullOrWhiteSpace(text[line.ContentStart..pipes[0]]);
        var pipe = hasLead ? column - 1 : column;
        return pipe < 0 || pipes.Count == 0 ? line.ContentStart : pipes[Math.Min(pipe, pipes.Count - 1)] + 1;
    }

    /// <summary>修改一个单元格：<paramref name="value"/> 是编辑时的文字，其它单元格的原文不变。</summary>
    public static MarkdownEdit SetCell(MarkdownModel model, string text, MarkdownTable table, int row, int column, string value)
    {
        var encoded = EncodeCell(value);
        var line = model.Lines[row == 0 ? table.HeaderLine : table.SeparatorLine + row];
        var pipes = ZulipMarkdown.TablePipes(text, line.ContentStart, line.End);
        var hasLead = pipes.Count > 0 && !string.IsNullOrWhiteSpace(text[line.ContentStart..pipes[0]]);
        if (!hasLead && column + 1 < pipes.Count)
        {
            var start = pipes[column] + 1;
            var replacement = $" {encoded} ";
            return new MarkdownEdit(start, pipes[column + 1] - start, replacement, start + 1 + encoded.Length, start + 1 + encoded.Length);
        }
        // 这一行没有这一格，或者不是 | 开头结尾：整个表格重新写出
        var data = Parse(model, text, table);
        data.Rows[row][column] = encoded;
        return Write(model, table, data, row, column);
    }

    private sealed record Data(List<List<string>> Rows, List<TableAlign> Aligns)
    {
        public int Columns => Aligns.Count;
    }

    /// <summary>光标所在的单元格。</summary>
    public static (int Row, int Column) CellAt(MarkdownModel model, string text, MarkdownTable table, int caret)
    {
        var index = Math.Clamp(model.LineIndexAt(caret), table.HeaderLine, table.LastLine);
        // 分隔行不显示，光标不会停在上面；万一在，算作标题行
        var row = index <= table.SeparatorLine ? 0 : index - table.SeparatorLine;
        var line = model.Lines[index == table.SeparatorLine ? table.HeaderLine : index];
        var pipes = ZulipMarkdown.TablePipes(text, line.ContentStart, line.End);
        var hasLead = pipes.Count > 0 && !string.IsNullOrWhiteSpace(text[line.ContentStart..pipes[0]]);
        var before = pipes.Count(p => p < caret);
        var columns = RowCells(text, line).Count;
        return (row, Math.Clamp(hasLead ? before : before - 1, 0, Math.Max(0, columns - 1)));
    }

    /// <summary>每列的对齐方式（来自分隔行）。</summary>
    public static IReadOnlyList<TableAlign> Aligns(MarkdownModel model, string text, MarkdownTable table)
    {
        var line = model.Lines[table.SeparatorLine];
        return RowCells(text, line).Select(ParseAlign).ToList();
    }

    public static int ColumnCount(MarkdownModel model, string text, MarkdownTable table) => Parse(model, text, table).Columns;

    /// <summary>行数（含标题行）。</summary>
    public static int RowCount(MarkdownTable table) => table.LastLine - table.SeparatorLine + 1;

    /// <summary>调整大小：缩小时多出来的内容直接丢弃，放大时补空单元格。</summary>
    public static MarkdownEdit? Resize(MarkdownModel model, string text, MarkdownTable table, int caret, int columns, int rows)
    {
        columns = Math.Clamp(columns, MinColumns, MaxColumns);
        rows = Math.Clamp(rows, MinRows, MaxRows);
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        if (data.Rows.Count > rows)
            data.Rows.RemoveRange(rows, data.Rows.Count - rows);
        while (data.Rows.Count < rows)
            data.Rows.Add(EmptyRow(data.Columns));
        if (data.Columns > columns)
        {
            data.Aligns.RemoveRange(columns, data.Columns - columns);
            foreach (var cells in data.Rows)
                cells.RemoveRange(columns, cells.Count - columns);
        }
        while (data.Columns < columns)
        {
            data.Aligns.Add(TableAlign.Left);
            foreach (var cells in data.Rows)
                cells.Add("");
        }
        return Write(model, table, data, Math.Min(row, rows - 1), Math.Min(column, columns - 1));
    }

    public static MarkdownEdit? SetAlign(MarkdownModel model, string text, MarkdownTable table, int caret, TableAlign align)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        if (data.Aligns[column] == align)
            return null;
        data.Aligns[column] = align;
        return Write(model, table, data, row, column);
    }

    public static MarkdownEdit? InsertRow(MarkdownModel model, string text, MarkdownTable table, int caret, bool below)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        if (data.Rows.Count >= MaxRows)
            return null;
        var at = below ? row + 1 : row;
        data.Rows.Insert(at, EmptyRow(data.Columns));
        return Write(model, table, data, at, column);
    }

    public static MarkdownEdit? InsertColumn(MarkdownModel model, string text, MarkdownTable table, int caret, bool right)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        if (data.Columns >= MaxColumns)
            return null;
        var at = right ? column + 1 : column;
        data.Aligns.Insert(at, TableAlign.Left);
        foreach (var cells in data.Rows)
            cells.Insert(at, "");
        return Write(model, table, data, row, at);
    }

    public static MarkdownEdit? MoveRow(MarkdownModel model, string text, MarkdownTable table, int caret, int delta)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        var target = row + delta;
        if (target < 0 || target >= data.Rows.Count)
            return null;
        (data.Rows[row], data.Rows[target]) = (data.Rows[target], data.Rows[row]);
        return Write(model, table, data, target, column);
    }

    public static MarkdownEdit? MoveColumn(MarkdownModel model, string text, MarkdownTable table, int caret, int delta)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        var target = column + delta;
        if (target < 0 || target >= data.Columns)
            return null;
        (data.Aligns[column], data.Aligns[target]) = (data.Aligns[target], data.Aligns[column]);
        foreach (var cells in data.Rows)
            (cells[column], cells[target]) = (cells[target], cells[column]);
        return Write(model, table, data, row, target);
    }

    /// <summary>删除光标所在的行；标题行删除后下一行成为标题；只剩一行时删除整个表格。</summary>
    public static MarkdownEdit? DeleteRow(MarkdownModel model, string text, MarkdownTable table, int caret)
    {
        var data = Parse(model, text, table);
        if (data.Rows.Count <= 1)
            return DeleteTable(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        data.Rows.RemoveAt(row);
        return Write(model, table, data, Math.Min(row, data.Rows.Count - 1), column);
    }

    public static MarkdownEdit? DeleteColumn(MarkdownModel model, string text, MarkdownTable table, int caret)
    {
        var data = Parse(model, text, table);
        if (data.Columns <= MinColumns)
            return null;
        var (row, column) = CellAt(model, text, table, caret);
        data.Aligns.RemoveAt(column);
        foreach (var cells in data.Rows)
            cells.RemoveAt(column);
        return Write(model, table, data, row, Math.Min(column, data.Columns - 1));
    }

    /// <summary>删除整个表格（连同它后面的换行；在文字末尾时连同前面的换行）。</summary>
    public static MarkdownEdit DeleteTable(MarkdownModel model, string text, MarkdownTable table)
    {
        var (start, end) = Range(model, table);
        if (end < text.Length && text[end] == '\n')
            end++;
        else if (start > 0 && text[start - 1] == '\n')
            start--;
        return new MarkdownEdit(start, end - start, "", start, start);
    }

    /// <summary>格式化源码：同一列补空格对齐（中文按两格）。</summary>
    public static MarkdownEdit? Format(MarkdownModel model, string text, MarkdownTable table, int caret)
    {
        var data = Parse(model, text, table);
        var (row, column) = CellAt(model, text, table, caret);
        return Write(model, table, data, row, column, padded: true);
    }

    /// <summary>整个表格的原文。</summary>
    public static string Source(MarkdownModel model, string text, MarkdownTable table)
    {
        var (start, end) = Range(model, table);
        return text[start..end];
    }

    /// <summary>新表格的 Markdown（第一行为标题，单元格都是空的）。</summary>
    public static string Empty(int columns, int rows)
    {
        columns = Math.Clamp(columns, MinColumns, MaxColumns);
        rows = Math.Clamp(rows, MinRows, MaxRows);
        var cells = Enumerable.Range(0, rows).Select(_ => EmptyRow(columns)).ToList();
        return Serialize(new Data(cells, Enumerable.Repeat(TableAlign.Left, columns).ToList()), false, 0, 0).Text;
    }

    /// <summary>
    /// 由单元格的 Markdown 写出表格（第一行为标题）。<paramref name="padded"/> 为 true 时同一列补空格对齐。
    /// 单元格不能含竖线和换行：转义为 <c>\|</c> 和 <c>&lt;br /&gt;</c>。列数以最多的一行为准。
    /// </summary>
    public static string FromCells(IReadOnlyList<IReadOnlyList<string>> rows, IReadOnlyList<TableAlign> aligns, bool padded)
    {
        var columns = rows.Max(r => r.Count);
        var cells = rows.Select(r =>
        {
            var row = r.Select(EscapeCell).ToList();
            row.AddRange(Enumerable.Repeat("", columns - row.Count));
            return row;
        }).ToList();
        var columnAligns = Enumerable.Range(0, columns).Select(c => c < aligns.Count ? aligns[c] : TableAlign.Left).ToList();
        return Serialize(new Data(cells, columnAligns), padded, 0, 0).Text;
    }

    private static string EscapeCell(string cell)
    {
        var sb = new StringBuilder(cell.Trim().ReplaceLineEndings("<br />"));
        for (var i = sb.Length - 1; i >= 0; i--)
        {
            if (sb[i] == '|' && (i == 0 || sb[i - 1] != '\\'))
                sb.Insert(i, '\\');
        }
        return sb.ToString();
    }

    private static List<string> EmptyRow(int columns) => Enumerable.Repeat("", columns).ToList();

    private static (int Start, int End) Range(MarkdownModel model, MarkdownTable table) =>
        (model.Lines[table.HeaderLine].Start, model.Lines[table.LastLine].End);

    private static Data Parse(MarkdownModel model, string text, MarkdownTable table)
    {
        var aligns = Aligns(model, text, table).ToList();
        var rows = new List<List<string>>();
        for (var index = table.HeaderLine; index <= table.LastLine; index++)
        {
            if (index != table.SeparatorLine)
                rows.Add(RowCells(text, model.Lines[index]).Select(c => c.Trim()).ToList());
        }
        // 列数以最多的一行为准；不够的补空
        var columns = Math.Max(aligns.Count, rows.Max(r => r.Count));
        while (aligns.Count < columns)
            aligns.Add(TableAlign.Left);
        foreach (var cells in rows)
        {
            while (cells.Count < columns)
                cells.Add("");
        }
        return new Data(rows, aligns);
    }

    // 一行的单元格原文（含两端空白）：第一个 | 之前和最后一个 | 之后只有空白时不是单元格
    private static List<string> RowCells(string text, MdLine line)
    {
        var pipes = ZulipMarkdown.TablePipes(text, line.ContentStart, line.End);
        var cells = new List<string>();
        var start = line.ContentStart;
        foreach (var pipe in pipes)
        {
            cells.Add(text[start..pipe]);
            start = pipe + 1;
        }
        cells.Add(text[start..line.End]);
        if (cells.Count > 1 && string.IsNullOrWhiteSpace(cells[^1]))
            cells.RemoveAt(cells.Count - 1);
        if (cells.Count > 1 && string.IsNullOrWhiteSpace(cells[0]))
            cells.RemoveAt(0);
        return cells;
    }

    private static TableAlign ParseAlign(string separator)
    {
        var s = separator.Trim();
        return s.EndsWith(':') ? s.StartsWith(':') ? TableAlign.Center : TableAlign.Right : TableAlign.Left;
    }

    private static MarkdownEdit Write(MarkdownModel model, MarkdownTable table, Data data, int row, int column, bool padded = false)
    {
        var (start, end) = Range(model, table);
        var (replacement, caret) = Serialize(data, padded, row, column);
        return new MarkdownEdit(start, end - start, replacement, start + caret, start + caret);
    }

    // 写出表格；Caret 为 (row, column) 单元格文字末尾在写出的文字中的位置
    private static (string Text, int Caret) Serialize(Data data, bool padded, int row, int column)
    {
        var widths = new int[data.Columns];
        if (padded)
        {
            for (var c = 0; c < data.Columns; c++)
                widths[c] = Math.Max(3, data.Rows.Max(r => DisplayWidth(r[c])));
        }

        var sb = new StringBuilder();
        var caret = 0;
        void WriteRow(List<string> cells, int rowIndex)
        {
            for (var c = 0; c < cells.Count; c++)
            {
                sb.Append("| ");
                sb.Append(cells[c]);
                if (rowIndex == row && c == column)
                    caret = sb.Length;
                sb.Append(' ', Math.Max(0, widths[c] - DisplayWidth(cells[c])));
                sb.Append(' ');
            }
            sb.Append('|');
        }

        WriteRow(data.Rows[0], 0);
        sb.Append('\n');
        for (var c = 0; c < data.Columns; c++)
        {
            sb.Append("| ");
            sb.Append(SeparatorCell(data.Aligns[c], Math.Max(3, widths[c])));
            sb.Append(' ');
        }
        sb.Append('|');
        for (var r = 1; r < data.Rows.Count; r++)
        {
            sb.Append('\n');
            WriteRow(data.Rows[r], r);
        }
        return (sb.ToString(), caret);
    }

    private static string SeparatorCell(TableAlign align, int width) => align switch
    {
        TableAlign.Center => ":" + new string('-', Math.Max(1, width - 2)) + ":",
        TableAlign.Right => new string('-', Math.Max(1, width - 1)) + ":",
        _ => new string('-', width),
    };

    private static int DisplayWidth(string s) => s.Sum(c => c >= 0x2E80 ? 2 : 1);
}
