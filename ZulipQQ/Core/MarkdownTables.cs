using System.Collections.Generic;

namespace ZulipQQ.Core;

/// <summary>
/// 输入框中的表格：<see cref="HeaderLine"/> 为标题行，紧接着是分隔行 <see cref="SeparatorLine"/>，
/// 到 <see cref="LastLine"/> 结束（行号都从 0 起）。
/// </summary>
public sealed record MarkdownTable(int HeaderLine, int SeparatorLine, int LastLine);

public static class MarkdownTables
{
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
