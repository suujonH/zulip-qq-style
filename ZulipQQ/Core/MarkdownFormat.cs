using System;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

public enum MarkdownFormatKind
{
    Bold, Italic, Strikethrough, Link, Code, Math,
    Quote, BulletedList, NumberedList,
    CodeBlock, MathBlock, QuoteBlock, Spoiler, Rule, Table,
}

/// <summary>一次修改：把 [Start, Start+Length) 换成 Replacement，之后选中 [SelectionStart, SelectionEnd)（文档绝对位置）。</summary>
public readonly record struct MarkdownEdit(int Start, int Length, string Replacement, int SelectionStart, int SelectionEnd);

/// <summary>
/// 输入框的格式指令：
/// 加粗、斜体等成对符号可以加上或取消（没有选中文字、光标在该格式中时取消光标所在的格式）；
/// 引用、列表、标题按行加上、去掉或互换；代码块、引用块、剧透、公式块用围栏包住选中的文字；分隔线、表格插入模板。
/// </summary>
public static partial class MarkdownFormat
{
    private const string LinkPlaceholder = "url";
    private const string SpoilerHeader = "Header";
    private const string TableTemplate = "| 标题 | 标题 |\n| --- | --- |\n| 内容 | 内容 |";

    /// <param name="model">文字的解析结果；给出时，没有选中文字且光标在该格式中会取消光标所在的格式。</param>
    public static MarkdownEdit Apply(string text, int selectionStart, int selectionEnd, MarkdownFormatKind kind,
        MarkdownModel? model = null)
    {
        var s = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var e = Math.Clamp(Math.Max(selectionStart, selectionEnd), s, text.Length);

        switch (kind)
        {
            case MarkdownFormatKind.BulletedList or MarkdownFormatKind.NumberedList:
                while (e > s && char.IsWhiteSpace(text[e - 1]))
                    e--;
                return Lines(text, s, e, lines => List(lines, kind == MarkdownFormatKind.NumberedList));
            case MarkdownFormatKind.Quote:
                while (e > s && char.IsWhiteSpace(text[e - 1]))
                    e--;
                return Lines(text, s, e, Quote);
            case MarkdownFormatKind.CodeBlock or MarkdownFormatKind.MathBlock or MarkdownFormatKind.QuoteBlock or MarkdownFormatKind.Spoiler:
                return Block(text, s, e, kind);
            case MarkdownFormatKind.Rule:
                return Rule(text, s, e);
            case MarkdownFormatKind.Table:
                return Table(text, s, e);
        }

        if (s == e && model is not null && ActiveSpan(model, s, kind) is { } span)
            return Remove(text, span, kind, s);

        // 选中链接中的一部分（如插入后选中的 url，或已输入的地址）：取消该链接，而不是再包一层
        if (kind == MarkdownFormatKind.Link && s < e && model is not null
            && model.Spans.Where(sp => sp.Kind == MdSpanKind.Link && sp.Start <= s && e <= sp.End && (sp.Start, sp.End) != (s, e))
                .MinBy(sp => sp.End - sp.Start) is { } link)
            return Remove(text, link, kind, e);

        // 没有选中文字：作用于光标所在的单词（与双击选中的范围相同，连续的中文算一个词），光标留在原来的文字处；
        // 链接选中占位的 url。光标在提及等整体元素中时不扩展（否则会拆坏它）
        if (s == e && WordAt(text, s) is var (ws, we) && we > ws
            && model?.Spans.Any(sp => ZulipMarkdown.IsAtomic(sp.Kind) && sp.Start < s && s < sp.End) != true)
        {
            var edit = Apply(text, ws, we, kind, model);
            if (kind == MarkdownFormatKind.Link)
                return edit;
            var caret = edit.SelectionStart + (s - ws);
            return edit with { SelectionStart = caret, SelectionEnd = caret };
        }

        // 成对符号与链接不包住两端的空白
        var (ts, te) = (s, e);
        while (ts < te && char.IsWhiteSpace(text[ts]))
            ts++;
        while (te > ts && char.IsWhiteSpace(text[te - 1]))
            te--;
        var selected = text[ts..te];

        return kind switch
        {
            MarkdownFormatKind.Code when selected.Contains('\n') => Block(text, s, e, MarkdownFormatKind.CodeBlock),
            MarkdownFormatKind.Math when selected.Contains('\n') => Block(text, s, e, MarkdownFormatKind.MathBlock),
            MarkdownFormatKind.Bold or MarkdownFormatKind.Italic => Stars(text, ts, te, kind == MarkdownFormatKind.Bold ? 2 : 1),
            MarkdownFormatKind.Strikethrough => Pair(text, ts, te, '~', 2),
            MarkdownFormatKind.Code => Pair(text, ts, te, '`', 1),
            MarkdownFormatKind.Math => Pair(text, ts, te, '$', 2),
            MarkdownFormatKind.Link => Link(text, ts, te),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>
    /// 把选中范围涉及的行设为 <paramref name="level"/> 级标题（0 为普通段落）；所有行已是该级标题时改回段落。
    /// 引用的 <c>&gt;</c> 保留在标题前。
    /// </summary>
    public static MarkdownEdit SetHeading(string text, int selectionStart, int selectionEnd, int level)
    {
        var s = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var e = Math.Clamp(Math.Max(selectionStart, selectionEnd), s, text.Length);
        while (e > s && char.IsWhiteSpace(text[e - 1]))
            e--;
        level = Math.Clamp(level, 0, 6);
        return Lines(text, s, e, lines =>
        {
            var skip = SkipBlank(lines);
            var target = lines.Where((l, i) => !skip(i)).All(l => HeadingLevel(l) == level) ? 0 : level;
            return lines.Select((line, i) =>
            {
                if (skip(i))
                    return line;
                var quote = QuotePrefixes().Match(line).Length;
                var rest = HeadingPrefix().Replace(line[quote..], "", 1);
                return line[..quote] + (target > 0 ? new string('#', target) + " " : "") + rest;
            }).ToArray();
        });
    }

    /// <summary>光标处是否已有该格式（格式按钮显示为按下）。</summary>
    public static bool IsActive(MarkdownModel model, int caret, MarkdownFormatKind kind)
    {
        if (SpanKinds(kind).Length > 0)
            return ActiveSpan(model, caret, kind) is not null;
        if (LineAt(model, caret) is not { } line)
            return false;
        return kind switch
        {
            MarkdownFormatKind.Quote => line.Prefixes.Any(p => p.Kind == MdPrefixKind.Quote),
            MarkdownFormatKind.BulletedList => line.Kind == MdLineKind.ListItem && line.Prefixes.Any(p => p.Kind == MdPrefixKind.Bullet),
            MarkdownFormatKind.NumberedList => line.Kind == MdLineKind.ListItem && line.Prefixes.Any(p => p.Kind == MdPrefixKind.Ordered),
            _ => false,
        };
    }

    /// <summary>光标所在行的标题级别；不是标题为 0。</summary>
    public static int HeadingLevelAt(MarkdownModel model, int caret) =>
        LineAt(model, caret) is { Kind: MdLineKind.Heading } line ? line.HeadingLevel : 0;

    /// <summary>插入全局时间 <c>&lt;time:ISO&gt; </c>（前面不是空白时先加空格），替换选中部分。</summary>
    public static MarkdownEdit InsertTime(string text, int selectionStart, int selectionEnd, DateTimeOffset time)
    {
        var s = Math.Clamp(Math.Min(selectionStart, selectionEnd), 0, text.Length);
        var e = Math.Clamp(Math.Max(selectionStart, selectionEnd), s, text.Length);
        var before = s > 0 && !char.IsWhiteSpace(text[s - 1]) ? " " : "";
        var insert = $"{before}<time:{time.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture)}> ";
        return new MarkdownEdit(s, e - s, insert, s + insert.Length, s + insert.Length);
    }

    /// <summary>
    /// 放入输入框的多行内容（粘贴、常用语、插件填入、编辑消息）：结尾不是换行时补一个换行，光标落在新的一行，
    /// 不会接在代码块、引用等的最后一行后面。单行内容原样返回。
    /// </summary>
    public static string WithTrailingLine(string content) =>
        content.Contains('\n') && !content.EndsWith('\n') ? content + "\n" : content;

    /// <summary>
    /// 与 Zulip 网页端 smart_insert_block 相同：把 <paramref name="block"/> 作为独立的段落放到 <c>[start, end)</c>，
    /// 不在开头时前面补足 <paramref name="paddingNewlines"/> 个换行，后面也补足（已有的换行计算在内）。返回要插入的文字。
    /// </summary>
    public static string PadBlock(string text, int start, int end, string block, int paddingNewlines = 2)
    {
        if (start > 0)
        {
            var before = 0;
            while (before < paddingNewlines && start - 1 - before >= 0 && text[start - 1 - before] == '\n')
                before++;
            block = new string('\n', paddingNewlines - before) + block;
        }
        var after = 0;
        while (after < paddingNewlines && end + after < text.Length && text[end + after] == '\n')
            after++;
        return block + new string('\n', paddingNewlines - after);
    }

    /// <summary>
    /// 与 Zulip 网页端 fenced_code.get_unused_fence 相同：比 <paramref name="content"/> 中行首（最多缩进 3 个空格）最长的 ``` 多一个 `，
    /// 至少 3 个。
    /// </summary>
    public static string UnusedFence(string content)
    {
        var length = 3;
        foreach (Match m in FenceLengthPattern().Matches(content))
            length = Math.Max(length, m.Groups[1].Length + 1);
        return new string('`', length);
    }

    // ───────────── 光标所在的格式 ─────────────

    private static MdSpanKind[] SpanKinds(MarkdownFormatKind kind) => kind switch
    {
        MarkdownFormatKind.Bold => [MdSpanKind.Strong, MdSpanKind.StrongEm],
        MarkdownFormatKind.Italic => [MdSpanKind.Em, MdSpanKind.StrongEm],
        MarkdownFormatKind.Strikethrough => [MdSpanKind.Del],
        MarkdownFormatKind.Code => [MdSpanKind.Code],
        MarkdownFormatKind.Math => [MdSpanKind.Math],
        MarkdownFormatKind.Link => [MdSpanKind.Link],
        _ => [],
    };

    // 光标在其中（不含两端）的该格式元素，有嵌套时取最内层
    private static MdSpan? ActiveSpan(MarkdownModel model, int caret, MarkdownFormatKind kind)
    {
        var kinds = SpanKinds(kind);
        return model.Spans.Where(s => kinds.Contains(s.Kind) && s.Start < caret && caret < s.End)
            .MinBy(s => s.End - s.Start);
    }

    private static MdLine? LineAt(MarkdownModel model, int caret) =>
        model.Lines.Count == 0 ? null : model.Lines[Math.Clamp(model.LineIndexAt(caret), 0, model.Lines.Count - 1)];

    private static (int Start, int End) WordAt(string text, int caret)
    {
        static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';
        var start = caret;
        while (start > 0 && IsWord(text[start - 1]))
            start--;
        var end = caret;
        while (end < text.Length && IsWord(text[end]))
            end++;
        return (start, end);
    }

    // 去掉元素的符号，光标留在原来的文字处。粗斜体（***）只去掉加粗或斜体的那部分；
    // 链接变为“文字 地址”（与选中整个链接时相同：地址是占位的 url 或与文字相同时只留文字，文字为空时只留地址）
    private static MarkdownEdit Remove(string text, MdSpan span, MarkdownFormatKind kind, int caret)
    {
        if (kind == MarkdownFormatKind.Link)
        {
            var label = text[span.ContentStart..span.ContentEnd];
            var url = span.Data ?? "";
            var keepUrl = label.Length > 0 && url.Length > 0 && url != LinkPlaceholder && url != label;
            var plain = label.Length == 0 ? url : keepUrl ? $"{label} {url}" : label;
            // 光标在文字中时位置不变；在地址中时落在留下的地址的同一位置，地址被去掉时落在文字末尾
            var urlStart = text.IndexOf(url, span.ContentEnd, StringComparison.Ordinal);
            var offset = caret <= span.ContentEnd || urlStart < 0 ? caret - span.ContentStart
                : keepUrl ? label.Length + 1 + caret - urlStart
                : label.Length == 0 ? caret - urlStart
                : label.Length;
            var linkCaret = span.Start + Math.Clamp(offset, 0, plain.Length);
            return new MarkdownEdit(span.Start, span.End - span.Start, plain, linkCaret, linkCaret);
        }

        var (open, close) = (kind, span.Kind) switch
        {
            (MarkdownFormatKind.Bold, MdSpanKind.StrongEm) => (2, 2),
            (MarkdownFormatKind.Italic, MdSpanKind.StrongEm) => (1, 1),
            _ => (span.ContentStart - span.Start, span.End - span.ContentEnd),
        };
        var inner = text[(span.Start + open)..(span.End - close)];
        var at = span.Start + Math.Clamp(caret - span.Start - open, 0, inner.Length);
        return new MarkdownEdit(span.Start, span.End - span.Start, inner, at, at);
    }

    // ───────────── 成对符号与链接 ─────────────

    // 加粗（2 个 *）与斜体（1 个 *）：按两侧连续的 * 个数判断是否已有该格式（*** 同时是加粗和斜体）
    private static MarkdownEdit Stars(string text, int s, int e, int marker)
    {
        bool Has(int count) => marker == 1 ? count is 1 or 3 : count is 2 or 3;

        var before = RunBefore(text, s, '*');
        var after = RunAfter(text, e, '*');
        if (before == after && Has(before))
            return Unwrap(text, s, e, marker);

        var selected = text[s..e];
        var lead = RunAfter(selected, 0, '*');
        var trail = RunBefore(selected, selected.Length, '*');
        if (lead == trail && selected.Length >= 2 * lead + 1 && Has(lead))
            return Strip(text, s, e, marker);

        return Wrap(text, s, e, new string('*', marker));
    }

    private static MarkdownEdit Pair(string text, int s, int e, char c, int marker)
    {
        if (RunBefore(text, s, c) == marker && RunAfter(text, e, c) == marker)
            return Unwrap(text, s, e, marker);
        var selected = text[s..e];
        if (selected.Length >= 2 * marker + 1 && RunAfter(selected, 0, c) == marker && RunBefore(selected, selected.Length, c) == marker)
            return Strip(text, s, e, marker);
        return Wrap(text, s, e, new string(c, marker));
    }

    private static int RunBefore(string text, int index, char c)
    {
        var n = 0;
        while (index - n - 1 >= 0 && text[index - n - 1] == c)
            n++;
        return n;
    }

    private static int RunAfter(string text, int index, char c)
    {
        var n = 0;
        while (index + n < text.Length && text[index + n] == c)
            n++;
        return n;
    }

    // 去掉选中文字外侧的符号
    private static MarkdownEdit Unwrap(string text, int s, int e, int marker) =>
        new(s - marker, e - s + 2 * marker, text[s..e], s - marker, e - marker);

    // 去掉选中文字内首尾的符号
    private static MarkdownEdit Strip(string text, int s, int e, int marker)
    {
        var inner = text[(s + marker)..(e - marker)];
        return new MarkdownEdit(s, e - s, inner, s, s + inner.Length);
    }

    private static MarkdownEdit Wrap(string text, int s, int e, string symbol) =>
        new(s, e - s, symbol + text[s..e] + symbol, s + symbol.Length, e + symbol.Length);

    // 选中整个链接时去掉链接（地址不是占位的 url 时保留在文字后）；否则包成 [文字](url) 并选中 url
    private static MarkdownEdit Link(string text, int s, int e)
    {
        var selected = text[s..e];
        if (LinkPattern().Match(selected) is { Success: true } m)
        {
            var url = m.Groups["url"].Value;
            var plain = url == LinkPlaceholder ? m.Groups["text"].Value : $"{m.Groups["text"].Value} {url}";
            return new MarkdownEdit(s, e - s, plain, s, s + plain.Length);
        }
        var replacement = $"[{selected}]({LinkPlaceholder})";
        var urlStart = s + selected.Length + 3;
        return new MarkdownEdit(s, e - s, replacement, urlStart, urlStart + LinkPlaceholder.Length);
    }

    // ───────────── 按行 ─────────────

    /// <summary>
    /// 作用于 [s, e) 涉及的整行，<paramref name="transform"/> 返回每行的新内容（行数不变）。
    /// 有选中文字时选中修改后的整段；没有时光标随所在行行首的增减移动（落在被删掉的符号中时移到行首）。
    /// </summary>
    private static MarkdownEdit Lines(string text, int s, int e, Func<string[], string[]> transform)
    {
        var lineStart = s == 0 ? 0 : text.LastIndexOf('\n', s - 1) + 1;
        var lineEnd = text.IndexOf('\n', e);
        if (lineEnd < 0)
            lineEnd = text.Length;
        var lines = text[lineStart..lineEnd].Split('\n');
        var result = transform(lines);
        var replacement = string.Join("\n", result);
        if (s != e)
            return new MarkdownEdit(lineStart, lineEnd - lineStart, replacement, lineStart, lineStart + replacement.Length);
        var column = s - lineStart + result[0].Length - lines[0].Length;
        var caret = lineStart + Math.Clamp(column, 0, result[0].Length);
        return new MarkdownEdit(lineStart, lineEnd - lineStart, replacement, caret, caret);
    }

    // 多行时跳过空行；只有一行（光标所在行）时空行也处理
    private static Func<int, bool> SkipBlank(string[] lines) =>
        i => lines.Length > 1 && string.IsNullOrWhiteSpace(lines[i]);

    // 所有行都已是目标列表时去掉标记，否则换成目标列表（编号从 1 递增）
    private static string[] List(string[] lines, bool numbered)
    {
        var skip = SkipBlank(lines);
        var target = numbered ? NumberedPattern() : BulletPattern();
        var other = numbered ? BulletPattern() : NumberedPattern();
        var remove = lines.Where((l, i) => !skip(i)).All(l => target.IsMatch(l));

        var result = new string[lines.Length];
        var number = 1;
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (skip(i))
            {
                result[i] = line;
                continue;
            }
            var m = target.Match(line) is { Success: true } tm ? tm : other.Match(line);
            var indent = m.Success ? m.Groups["indent"].Value : line[..(line.Length - line.TrimStart(' ', '\t').Length)];
            var body = m.Success ? line[m.Length..] : line[indent.Length..];
            var sb = new StringBuilder(indent);
            if (!remove)
                sb.Append(numbered ? $"{number++}. " : "- ");
            result[i] = sb.Append(body).ToString();
        }
        return result;
    }

    // 所有行都是引用时去掉一层 >，否则每行加上 "> "
    private static string[] Quote(string[] lines)
    {
        var skip = SkipBlank(lines);
        var remove = lines.Where((l, i) => !skip(i)).All(l => QuoteLinePattern().IsMatch(l));
        return lines.Select((line, i) => skip(i) ? line : remove ? QuoteLinePattern().Replace(line, "", 1) : "> " + line).ToArray();
    }

    private static int HeadingLevel(string line)
    {
        var rest = line[QuotePrefixes().Match(line).Length..];
        return HeadingPrefix().Match(rest) is { Success: true } m ? m.Groups["hashes"].Length : 0;
    }

    // ───────────── 块 ─────────────

    // 代码块、引用块、剧透、公式块：围栏单独成行，选中文字中有 ``` 时围栏加长
    private static MarkdownEdit Block(string text, int s, int e, MarkdownFormatKind kind)
    {
        while (s < e && text[s] == '\n')
            s++;
        while (e > s && text[e - 1] == '\n')
            e--;
        var selected = text[s..e];
        var fence = "```";
        while (selected.Contains(fence, StringComparison.Ordinal))
            fence += "`";
        var open = kind switch
        {
            MarkdownFormatKind.QuoteBlock => fence + "quote",
            MarkdownFormatKind.Spoiler => $"{fence}spoiler {SpoilerHeader}",
            MarkdownFormatKind.MathBlock => fence + "math",
            _ => fence,
        };
        var before = s > 0 && text[s - 1] != '\n' ? "\n" : "";
        var after = e < text.Length && text[e] != '\n' ? "\n" : "";
        var replacement = $"{before}{open}\n{selected}\n{fence}{after}";

        var openStart = s + before.Length;
        var contentStart = openStart + open.Length + 1;
        var (selStart, selEnd) = kind switch
        {
            MarkdownFormatKind.CodeBlock => (openStart + fence.Length, openStart + fence.Length),
            MarkdownFormatKind.Spoiler => (openStart + fence.Length + "spoiler ".Length, openStart + open.Length),
            _ => (contentStart, contentStart + selected.Length),
        };
        return new MarkdownEdit(s, e - s, replacement, selStart, selEnd);
    }

    // 分隔线：前面必须是空行（否则 --- 会把上一行变成二级标题），光标放到下一行
    private static MarkdownEdit Rule(string text, int s, int e)
    {
        var before = BlankLineBefore(text, s);
        var lineBreakAfter = e < text.Length && text[e] == '\n';
        var replacement = before + "---" + (lineBreakAfter ? "" : "\n");
        var caret = s + replacement.Length + (lineBreakAfter ? 1 : 0);
        return new MarkdownEdit(s, e - s, replacement, caret, caret);
    }

    // 表格：前后与其它文字之间空一行，选中第一个“标题”
    private static MarkdownEdit Table(string text, int s, int e)
    {
        var before = BlankLineBefore(text, s);
        var after = e >= text.Length ? ""
            : text[e] != '\n' ? "\n\n"
            : e + 1 < text.Length && text[e + 1] != '\n' ? "\n"
            : "";
        var replacement = before + TableTemplate + after;
        var start = s + before.Length + 2;
        return new MarkdownEdit(s, e - s, replacement, start, start + 2);
    }

    // 在 s 处开始新的块需要补的换行：s 前一行有文字时补到空一行
    private static string BlankLineBefore(string text, int s) =>
        s == 0 ? ""
        : text[s - 1] != '\n' ? "\n\n"
        : s >= 2 && text[s - 2] != '\n' ? "\n"
        : "";

    [GeneratedRegex(@"^ {0,3}(`{3,})", RegexOptions.Multiline)]
    private static partial Regex FenceLengthPattern();

    [GeneratedRegex(@"^\[(?<text>.*)\]\((?<url>.*)\)$")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^(?<indent>[ \t]*)[-*+] ")]
    private static partial Regex BulletPattern();

    [GeneratedRegex(@"^(?<indent>[ \t]*)\d+\. ")]
    private static partial Regex NumberedPattern();

    [GeneratedRegex(@"^ {0,3}> ?")]
    private static partial Regex QuoteLinePattern();

    [GeneratedRegex(@"^(?: {0,3}> ?)*")]
    private static partial Regex QuotePrefixes();

    [GeneratedRegex(@"^(?<hashes>#{1,6})(?:[ \t]+|$)")]
    private static partial Regex HeadingPrefix();
}
