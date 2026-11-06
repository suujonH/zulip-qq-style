using System.Collections.Generic;

namespace ZulipQQ.Core;

/// <summary>
/// 输入框中的表格：<see cref="HeaderLine"/> 为标题行，紧接着是分隔行 <see cref="SeparatorLine"/>，
/// 到 <see cref="LastLine"/> 结束（行号都从 0 起）。
/// </summary>
public sealed record MarkdownTable(int HeaderLine, int SeparatorLine, int LastLine);

public static class MarkdownTables
{
    /// <summary>
    /// 输入框中的列宽：各列平分 <paramref name="available"/>；文字放不下平分宽度的列取文字需要的宽度，其它列平分剩下的。
    /// 全部加起来放不下时，各列按能缩小的量同比例缩小（文字自动换行），但不小于 <paramref name="minimum"/>（最长的不能断开的一段）；
    /// 到了最小宽度还放不下时每列取最小宽度（总宽度超过 <paramref name="available"/>，表格横向滚动）。
    /// </summary>
    public static double[] FitColumns(double[] required, double available, double[]? minimum = null)
    {
        minimum ??= required;
        var widths = (double[])required.Clone();
        double totalRequired = 0, totalMinimum = 0;
        for (var c = 0; c < required.Length; c++)
        {
            totalRequired += required[c];
            totalMinimum += minimum[c];
        }
        if (totalMinimum >= available)
            return (double[])minimum.Clone();
        if (totalRequired > available)
        {
            var ratio = (totalRequired - available) / (totalRequired - totalMinimum);
            for (var c = 0; c < required.Length; c++)
                widths[c] = required[c] - (required[c] - minimum[c]) * ratio;
            return widths;
        }
        var open = new List<int>();
        for (var c = 0; c < required.Length; c++)
            open.Add(c);
        var remaining = available;
        while (open.Count > 0)
        {
            var share = remaining / open.Count;
            var wide = open.FindAll(c => required[c] > share);
            if (wide.Count == 0)
            {
                foreach (var c in open)
                    widths[c] = share;
                break;
            }
            foreach (var c in wide)
            {
                remaining -= required[c];
                open.Remove(c);
            }
        }
        return widths;
    }

    /// <summary>找出解析结果中的表格。引用（"> " 或 quote 围栏）中的表格不算，按原文显示。</summary>
    public static IReadOnlyList<MarkdownTable> Find(MarkdownModel model)
    {
        var result = new List<MarkdownTable>();
        var lines = model.Lines;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            if (lines[i].Kind != MdLineKind.TableRow || lines[i + 1].Kind != MdLineKind.TableSeparator)
                continue;
            var last = i + 1;
            while (last + 1 < lines.Count && lines[last + 1].Kind == MdLineKind.TableRow)
                last++;
            var quoted = false;
            for (var k = i; k <= last; k++)
                quoted |= lines[k].QuoteDepth > 0;
            if (!quoted)
                result.Add(new MarkdownTable(i, i + 1, last));
            i = last;
        }
        return result;
    }
}
