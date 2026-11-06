using System;
using System.Collections.Generic;

namespace ZulipQQ.Core;

/// <summary>断行点的种类，决定断行处的那个字符怎么显示。</summary>
public enum SoftBreakKind
{
    /// <summary>空格（可以是连续的几个）：留在上一行的末尾，不显示，光标不能停在其中。</summary>
    Space,

    /// <summary>符号留在上一行的末尾，在它之后断行。</summary>
    After,

    /// <summary>左括号到下一行的开头，在它之前断行。</summary>
    Before,
}

/// <summary>可以断行的位置：<c>[Start, Start + Length)</c> 是相关的字符（行内偏移）。</summary>
public readonly record struct SoftBreak(int Start, int Length, SoftBreakKind Kind)
{
    /// <summary>上一行显示的文字到这里结束。</summary>
    public int TextEnd => Kind == SoftBreakKind.After ? Start + Length : Start;

    /// <summary>下一行显示的文字从这里开始。</summary>
    public int NextStart => Kind == SoftBreakKind.Before ? Start : Start + Length;
}

/// <summary>
/// 输入框表格中单元格的自动换行：只用于显示，文档不变。只在空格（全角、半角）和
/// 左括号、减号、问号、感叹号、逗号、冒号（全角、半角）、全角句号处断行；半角句号可能是小数点，不断行。
/// </summary>
public static class MarkdownTableWrap
{
    private const string Spaces = " 　";
    private const string OpenBrackets = "[(<{［（＜｛";
    private const string BreakAfter = "-－?？!！,，:：。";

    /// <summary>这一行所有可以断行的位置；<paramref name="isProtected"/> 为 true 的字符（代码、链接、提及等的内部）不断行。</summary>
    public static List<SoftBreak> Candidates(string line, Func<int, bool>? isProtected = null)
    {
        var result = new List<SoftBreak>();
        for (var i = 0; i < line.Length; i++)
        {
            if (isProtected?.Invoke(i) == true)
                continue;
            var c = line[i];
            if (Spaces.Contains(c))
            {
                var end = i + 1;
                while (end < line.Length && Spaces.Contains(line[end]) && isProtected?.Invoke(end) != true)
                    end++;
                // 行首和行尾的空格后面或前面没有东西，在那里断行没有意义
                if (i > 0 && end < line.Length)
                    result.Add(new SoftBreak(i, end - i, SoftBreakKind.Space));
                i = end - 1;
            }
            else if (OpenBrackets.Contains(c))
            {
                // 前面是空格或另一个左括号时，那里已经可以断行或者不该断开
                if (i > 0 && !Spaces.Contains(line[i - 1]) && !OpenBrackets.Contains(line[i - 1]))
                    result.Add(new SoftBreak(i, 1, SoftBreakKind.Before));
            }
            else if (BreakAfter.Contains(c))
            {
                // 连续的符号（“？！”）和后面的空格、右侧没有内容时不断开
                var next = i + 1 < line.Length ? line[i + 1] : '\0';
                if (next != '\0' && !Spaces.Contains(next) && !BreakAfter.Contains(next))
                    result.Add(new SoftBreak(i, 1, SoftBreakKind.After));
            }
        }
        return result;
    }

    /// <summary>实际断行的位置：一行放得下时不断，放不下时在最后一个放得下的位置断开。一段比 <paramref name="width"/> 还长时单独占一行。</summary>
    public static List<SoftBreak> Choose(string line, IReadOnlyList<SoftBreak> candidates, Func<string, double> measure, double width)
    {
        var chosen = new List<SoftBreak>();
        var start = 0;
        for (var i = 0; i < candidates.Count; i++)
        {
            var end = i + 1 < candidates.Count ? candidates[i + 1].TextEnd : line.Length;
            if (end > start && measure(line[start..end]) > width && candidates[i].TextEnd > start)
            {
                chosen.Add(candidates[i]);
                start = candidates[i].NextStart;
            }
        }
        return chosen;
    }

    /// <summary>断开后最宽的一段；<paramref name="breaks"/> 是全部可以断行的位置时，就是这一行最少需要的宽度。</summary>
    public static double Widest(string line, IReadOnlyList<SoftBreak> breaks, Func<string, double> measure)
    {
        var widest = 0.0;
        var start = 0;
        foreach (var b in breaks)
        {
            if (b.TextEnd > start)
                widest = Math.Max(widest, measure(line[start..b.TextEnd]));
            start = b.NextStart;
        }
        return Math.Max(widest, measure(line[start..]));
    }
}
