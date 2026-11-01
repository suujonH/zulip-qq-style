using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ZulipQQ.Core;

/// <summary>
/// 段落中一段文字的来源（由消息 HTML 得到），用于把选中的部分转回 Markdown。[Start, End) 为段落文字中的区间。
/// <see cref="Markdown"/> 不为 null 的是整体元素（提及、表情、时间、图片、公式），选中其中任何部分都复制整个 Markdown；
/// 其余为普通文字，按格式加上符号。
/// </summary>
public sealed record InlinePiece(int Start, int End, string? Markdown,
    bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Href = null);

/// <summary>把消息中选中的文字转为 Markdown（复制后粘贴到输入框，发送的格式与原消息相同）。</summary>
public static class InlineMarkdown
{
    /// <summary>
    /// 段落文字 <paramref name="text"/> 中 <c>[start, end)</c> 的 Markdown。不属于任何片段的文字（换行等）原样保留；
    /// 格式相同的相邻文字合并后只加一次符号；符号不包住两端的空白（否则服务器不认为是格式）。
    /// </summary>
    public static string FromPieces(string text, IReadOnlyList<InlinePiece> pieces, int start, int end)
    {
        start = Math.Clamp(start, 0, text.Length);
        end = Math.Clamp(end, start, text.Length);
        var sb = new StringBuilder();
        var group = new StringBuilder();
        InlinePiece? groupStyle = null;
        var pos = start;

        void Flush()
        {
            if (groupStyle is not null && group.Length > 0)
                sb.Append(Wrap(group.ToString(), groupStyle));
            group.Clear();
            groupStyle = null;
        }

        foreach (var p in pieces.Where(p => p.End > start && p.Start < end).OrderBy(p => p.Start))
        {
            if (p.Start > pos)
            {
                Flush();
                sb.Append(text, pos, p.Start - pos);
            }
            if (p.Markdown is { } markdown)
            {
                Flush();
                sb.Append(markdown);
            }
            else
            {
                if (groupStyle is not null && !SameStyle(groupStyle, p))
                    Flush();
                groupStyle ??= p;
                var from = Math.Max(p.Start, start);
                group.Append(text, from, Math.Min(p.End, end) - from);
            }
            pos = Math.Max(pos, Math.Min(p.End, end));
        }
        Flush();
        if (pos < end)
            sb.Append(text, pos, end - pos);
        return sb.ToString();
    }

    /// <summary>代码块中选中的文字：多行或整个代码块为围栏代码块，单行的一部分为行内代码。</summary>
    public static string FromCode(string code, string? language, int start, int end)
    {
        start = Math.Clamp(start, 0, code.Length);
        end = Math.Clamp(end, start, code.Length);
        var selected = code[start..end];
        if (!selected.Contains('\n') && (start > 0 || end < code.Length))
            return CodeSpan(selected);
        var fence = new string('`', Math.Max(3, LongestRun(selected, '`') + 1));
        return $"{fence}{language}\n{selected}\n{fence}";
    }

    private static bool SameStyle(InlinePiece a, InlinePiece b) =>
        a.Bold == b.Bold && a.Italic == b.Italic && a.Strike == b.Strike && a.Code == b.Code && a.Href == b.Href;

    private static string Wrap(string s, InlinePiece style)
    {
        // 行内代码中的空白有意义，整个包住
        var core = style.Code ? s : s.Trim();
        if (core.Trim().Length == 0)
            return s;
        var lead = s[..s.IndexOf(core, StringComparison.Ordinal)];
        var trail = s[(lead.Length + core.Length)..];
        if (style.Code)
            core = CodeSpan(core);
        if (style.Strike)
            core = $"~~{core}~~";
        if (style.Italic)
            core = $"*{core}*";
        if (style.Bold)
            core = $"**{core}**";
        if (style.Href is { } href)
            core = core == href ? href : $"[{core}]({href})";
        return lead + core + trail;
    }

    // 内容中有反引号时用更长的反引号包围；内容以反引号开头或结尾时加空格
    private static string CodeSpan(string code)
    {
        var ticks = new string('`', LongestRun(code, '`') + 1);
        var pad = code.StartsWith('`') || code.EndsWith('`') ? " " : "";
        return $"{ticks}{pad}{code}{pad}{ticks}";
    }

    private static int LongestRun(string s, char c)
    {
        int longest = 0, run = 0;
        foreach (var ch in s)
        {
            run = ch == c ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }
        return longest;
    }
}
