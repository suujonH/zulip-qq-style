using System;
using System.Collections.Generic;
using System.Linq;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 输入框中表格的排版：单元格的文字仍是文档中的文字（可以直接编辑），每个 | 显示为一段空白，
/// 宽度正好让下一格的文字按对齐方式落在所在列。列宽取这一列最宽的单元格，网格线由 <see cref="MarkdownBackgroundRenderer"/> 画。
/// </summary>
internal sealed class MarkdownTableLayout
{
    /// <summary>编辑中的表格上方留给工具栏（<see cref="MarkdownTableToolbar"/>）的高度。</summary>
    public const double ToolbarHeight = 30;

    private const double CellPadding = 8;
    // 空单元格也要点得到
    private const double MinColumnWidth = 48;

    private readonly Dictionary<int, Row> _rows = [];

    private MarkdownTableLayout(double[] edges, double rowHeight, double baseline)
    {
        Edges = edges;
        RowHeight = rowHeight;
        Baseline = baseline;
    }

    /// <summary>竖线的位置（比列数多一个），最后一个也是表格的宽度。</summary>
    public double[] Edges { get; }

    public double RowHeight { get; }

    public double Baseline { get; }

    public double Width => Edges[^1];

    /// <summary>这一行每个 | 的位置与显示的宽度。</summary>
    public IReadOnlyList<(int Offset, double Width)> Pipes(int lineIndex) => _rows[lineIndex].Placed;

    /// <summary>横坐标 <paramref name="x"/> 所在的列。</summary>
    public int ColumnAt(double x)
    {
        for (var c = 0; c + 1 < Edges.Length; c++)
        {
            if (x < Edges[c + 1])
                return c;
        }
        return Edges.Length - 2;
    }

    /// <summary>这一行第 <paramref name="column"/> 列放光标的位置（文字末尾）；这一行没有这一列时为 null。</summary>
    public int? CaretInCell(int lineIndex, int column) =>
        _rows.TryGetValue(lineIndex, out var row) && column < row.CellCarets.Count ? row.CellCarets[column] : null;

    /// <param name="measure">单元格原文显示的宽度，第二个参数为是否粗体（标题行）。</param>
    public static MarkdownTableLayout Build(MarkdownTable table, MarkdownModel model, string text,
        Func<string, bool, double> measure, double lineHeight, double baseline)
    {
        var rows = new List<Row>();
        for (var index = table.HeaderLine; index <= table.LastLine; index++)
        {
            if (index != table.SeparatorLine)
                rows.Add(Row.Parse(index, model.Lines[index], text, index == table.HeaderLine, measure));
        }

        var columnWidths = new double[Math.Max(1, rows.Max(r => r.CellCarets.Count))];
        Array.Fill(columnWidths, MinColumnWidth);
        foreach (var row in rows)
            row.Widen(columnWidths);
        var edges = new double[columnWidths.Length + 1];
        for (var c = 0; c < columnWidths.Length; c++)
            edges[c + 1] = edges[c] + columnWidths[c] + 2 * CellPadding;

        var aligns = MarkdownTableEdit.Aligns(model, text, table);
        var padding = (lineHeight * 0.3 + 2) / 2;
        var layout = new MarkdownTableLayout(edges, lineHeight + 2 * padding, baseline + padding);
        foreach (var row in rows)
        {
            row.Place(edges, aligns);
            layout._rows[row.Index] = row;
        }
        return layout;
    }

    // 一行：第一个 | 之前的文字 Lead，每个 | 和它后面的文字；最后一个 | 后面只有空白时那段不是单元格
    private sealed class Row
    {
        private readonly List<int> _pipeOffsets = [];
        private readonly List<double> _textAfter = [];
        private double _lead;
        private bool _leadIsCell;
        private bool _endsWithPipe;

        public int Index { get; private init; }

        public List<int> CellCarets { get; } = [];

        public IReadOnlyList<(int Offset, double Width)> Placed { get; private set; } = [];

        public static Row Parse(int index, MdLine line, string text, bool bold, Func<string, bool, double> measure)
        {
            var row = new Row { Index = index };
            var pipes = ZulipMarkdown.TablePipes(text, line.ContentStart, line.End);
            var leadEnd = pipes.Count > 0 ? pipes[0] : line.End;
            var lead = text[line.ContentStart..leadEnd];
            row._lead = measure(lead, bold);
            row._leadIsCell = !string.IsNullOrWhiteSpace(lead);
            if (row._leadIsCell)
                row.CellCarets.Add(CaretOf(lead, line.ContentStart));
            for (var k = 0; k < pipes.Count; k++)
            {
                var start = pipes[k] + 1;
                var cell = text[start..(k + 1 < pipes.Count ? pipes[k + 1] : line.End)];
                row._pipeOffsets.Add(pipes[k]);
                row._textAfter.Add(measure(cell, bold));
                if (k == pipes.Count - 1 && string.IsNullOrWhiteSpace(cell))
                    row._endsWithPipe = true;
                else
                    row.CellCarets.Add(CaretOf(cell, start));
            }
            return row;
        }

        // 文字末尾；空单元格在第一个空格之后（与 MarkdownTableEdit 写出的位置相同）
        private static int CaretOf(string cell, int start) =>
            string.IsNullOrWhiteSpace(cell) ? start + Math.Min(1, cell.Length) : start + cell.TrimEnd().Length;

        public void Widen(double[] columns)
        {
            var column = 0;
            if (_leadIsCell)
            {
                columns[0] = Math.Max(columns[0], _lead);
                column = 1;
            }
            for (var k = 0; k < _pipeOffsets.Count && column < columns.Length; k++)
            {
                if (k == _pipeOffsets.Count - 1 && _endsWithPipe)
                    break;
                columns[column] = Math.Max(columns[column], _textAfter[k]);
                column++;
            }
        }

        public void Place(double[] edges, IReadOnlyList<TableAlign> aligns)
        {
            var result = new List<(int, double)>(_pipeOffsets.Count);
            var x = _lead;
            var column = _leadIsCell ? 1 : 0;
            var columns = edges.Length - 1;
            for (var k = 0; k < _pipeOffsets.Count; k++)
            {
                var isEnd = k == _pipeOffsets.Count - 1 && _endsWithPipe;
                var at = Math.Min(column, columns);
                double target;
                if (isEnd || at == columns)
                {
                    // 结尾的 | 到这一格的右边线
                    target = edges[at];
                }
                else
                {
                    // 其它的 | 到下一格文字的开始
                    var room = edges[at + 1] - edges[at] - 2 * CellPadding - _textAfter[k];
                    var factor = (at < aligns.Count ? aligns[at] : TableAlign.Left) switch
                    {
                        TableAlign.Center => 0.5,
                        TableAlign.Right => 1,
                        _ => 0,
                    };
                    target = edges[at] + CellPadding + Math.Max(0, room) * factor;
                }
                result.Add((_pipeOffsets[k], Math.Max(0, target - x)));
                x = Math.Max(x, target) + _textAfter[k];
                column++;
            }
            Placed = result;
        }
    }
}
