using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

public enum MdLineKind { Paragraph, Blank, Heading, ListItem, Rule, FenceOpen, FenceClose, FenceBody, IndentedCode, TableRow, TableSeparator }

public enum MdFenceKind { Code, Quote, Spoiler, Math }

/// <summary>文档中的一行。偏移都是相对整个文档的绝对位置；End 不含换行符。</summary>
public sealed record MdLine(int Start, int End, MdLineKind Kind)
{
    /// <summary>行首要隐藏 / 替换的前缀区间（引用的每个 "> "、标题的 "## "、列表的 "- " / "1. "），按位置排列。</summary>
    public IReadOnlyList<MdPrefix> Prefixes { get; init; } = [];

    /// <summary>行内容（去掉前缀后）的起点，行内解析从这里开始。</summary>
    public int ContentStart { get; init; }

    /// <summary>标题级别 1～6，其它行为 0。</summary>
    public int HeadingLevel { get; init; }

    /// <summary>"> " 层数加上外层 <c>```quote</c> 的层数。</summary>
    public int QuoteDepth { get; init; }

    /// <summary>所在（或本行打开 / 关闭的）围栏种类；不在围栏中为 null。</summary>
    public MdFenceKind? Fence { get; init; }

    /// <summary>FenceOpen 行：代码语言或剧透标题。</summary>
    public string? FenceInfo { get; init; }
}

public enum MdPrefixKind { Quote, Heading, Bullet, Ordered }

public readonly record struct MdPrefix(MdPrefixKind Kind, int Start, int End);

public enum MdSpanKind { Code, StrongEm, Strong, Em, Del, Math, Link, Url, UserMention, GroupMention, ChannelLink, Time, Emoji, UploadImage, UploadFile, UploadPlaceholder }

/// <summary>
/// 行内元素：[Start, End) 为整个元素，[ContentStart, ContentEnd) 为显示的内容；两侧为符号。
/// Data：Link / Url / UploadImage / UploadFile 为地址；提及 / 频道为显示文字；Emoji 为名称；Time 为原始时间文字；
/// UploadPlaceholder 为文件名；Code / Math 等为 null。
/// </summary>
public sealed record MdSpan(MdSpanKind Kind, int Start, int End, int ContentStart, int ContentEnd, string? Data);

/// <summary>解析结果。<see cref="Spans"/> 按 Start 升序。</summary>
public sealed record MarkdownModel(IReadOnlyList<MdLine> Lines, IReadOnlyList<MdSpan> Spans)
{
    public static MarkdownModel Empty { get; } = new([], []);

    /// <summary><paramref name="offset"/> 所在的行号（0 起）；没有行时为 -1。</summary>
    public int LineIndexAt(int offset)
    {
        int lo = 0, hi = Lines.Count - 1, found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (Lines[mid].Start <= offset)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }
        return found;
    }
}

/// <summary>判断提及、频道、表情是否存在（服务器找不到时按普通文字处理）。</summary>
public interface IMarkdownResolver
{
    bool HasUser(string name, long? id);
    bool HasGroup(string name);
    bool HasChannel(string name);
    bool HasEmoji(string name);
}

/// <summary>
/// 按 Zulip 服务器的 Markdown 规则（Python-Markdown 加 zerver/lib/markdown 的自定义规则）标出块与行内元素的区间，
/// 供输入框显示格式。不生成 HTML。与服务器一致的要点：没有反斜杠转义、下划线不是强调、加粗等不能跨行、
/// 找不到的用户 / 频道 / 表情按普通文字。
/// </summary>
public static partial class ZulipMarkdown
{
    /// <summary>已被高优先级元素占用的字符（相当于服务器的占位符）：不是空白也不是 Markdown 符号。</summary>
    private const char Masked = '\u0002';

    private static readonly string[] Wildcards = ["all", "everyone", "channel", "stream", "topic"];

    public static MarkdownModel Parse(string text, IMarkdownResolver resolver)
    {
        var lines = ParseLines(text);
        var spans = new List<MdSpan>();
        foreach (var line in lines)
        {
            switch (line.Kind)
            {
                case MdLineKind.Paragraph or MdLineKind.Heading or MdLineKind.ListItem:
                    ParseInline(text, line.ContentStart, line.End, resolver, spans);
                    break;
                case MdLineKind.TableRow:
                    foreach (var (a, b) in TableCells(text, line.ContentStart, line.End))
                        ParseInline(text, a, b, resolver, spans);
                    break;
            }
        }
        spans.Sort((x, y) => x.Start.CompareTo(y.Start));
        return new MarkdownModel(lines, spans);
    }

    /// <summary>提及（用户与用户组）的区间，供 <see cref="MentionText.Mask"/> 遮盖。</summary>
    public static IReadOnlyList<MentionSpan> MentionSpans(MarkdownModel model, string text) =>
        model.Spans
            .Where(s => s.Kind is MdSpanKind.UserMention or MdSpanKind.GroupMention && s.End <= text.Length)
            .Select(s => new MentionSpan(s.Start, s.End - s.Start, text[s.Start..s.End]))
            .ToList();

    /// <summary>在输入框中作为一个整体显示的元素（光标进入时也不展开符号）。</summary>
    public static bool IsAtomic(MdSpanKind kind) =>
        kind is MdSpanKind.UserMention or MdSpanKind.GroupMention or MdSpanKind.ChannelLink or MdSpanKind.Time
            or MdSpanKind.Emoji or MdSpanKind.UploadImage or MdSpanKind.UploadFile or MdSpanKind.UploadPlaceholder;

    // ───────────── 块 ─────────────

    private static List<MdLine> ParseLines(string text)
    {
        var raw = new List<(int Start, int End)>();
        var pos = 0;
        while (true)
        {
            var nl = text.IndexOf('\n', pos);
            if (nl < 0)
            {
                raw.Add((pos, text.Length));
                break;
            }
            raw.Add((pos, nl));
            pos = nl + 1;
        }

        var result = new List<MdLine>(raw.Count);
        var fences = new List<(string Fence, MdFenceKind Kind)>();
        MdLineKind? prevKind = null;
        MdLineKind? lastNonBlank = null;
        var tableSeparatorAt = -1;
        var inTable = false;

        for (var i = 0; i < raw.Count; i++)
        {
            var (start, end) = raw[i];
            var line = text[start..end];
            var quotes = fences.Count(f => f.Kind == MdFenceKind.Quote);
            MdLine md;

            if (fences.Count > 0 && fences[^1].Kind is MdFenceKind.Code or MdFenceKind.Math)
            {
                var top = fences[^1];
                if (line.TrimEnd() == top.Fence)
                {
                    md = new MdLine(start, end, MdLineKind.FenceClose) { ContentStart = start, Fence = top.Kind, QuoteDepth = quotes };
                    fences.RemoveAt(fences.Count - 1);
                }
                else
                {
                    md = new MdLine(start, end, MdLineKind.FenceBody) { ContentStart = start, Fence = top.Kind, QuoteDepth = quotes };
                }
                inTable = false;
            }
            else if (fences.Count > 0 && line.TrimEnd() == fences[^1].Fence)
            {
                var top = fences[^1];
                md = new MdLine(start, end, MdLineKind.FenceClose) { ContentStart = start, Fence = top.Kind, QuoteDepth = quotes };
                fences.RemoveAt(fences.Count - 1);
                inTable = false;
            }
            else if (FencePattern().Match(line) is { Success: true } fm)
            {
                var lang = fm.Groups["lang"].Success ? fm.Groups["lang"].Value : "";
                var header = fm.Groups["header"].Success ? fm.Groups["header"].Value.Trim() : "";
                var (kind, info) = lang.ToLowerInvariant() switch
                {
                    "quote" or "quoted" => (MdFenceKind.Quote, (string?)null),
                    "spoiler" => (MdFenceKind.Spoiler, header),
                    "math" => (MdFenceKind.Math, null),
                    _ => (MdFenceKind.Code, lang),
                };
                fences.Add((fm.Groups["fence"].Value, kind));
                md = new MdLine(start, end, MdLineKind.FenceOpen)
                {
                    ContentStart = start, Fence = kind, FenceInfo = info,
                    QuoteDepth = quotes + (kind == MdFenceKind.Quote ? 1 : 0),
                };
                inTable = false;
            }
            else
            {
                md = ClassifyLine(text, raw, i, prevKind, lastNonBlank, ref tableSeparatorAt, ref inTable);
                var outer = fences.Count > 0 ? fences[^1].Kind : (MdFenceKind?)null;
                md = md with { QuoteDepth = md.QuoteDepth + quotes, Fence = outer };
            }

            result.Add(md);
            prevKind = md.Kind;
            if (md.Kind != MdLineKind.Blank)
                lastNonBlank = md.Kind;
        }
        return result;
    }

    // 不在代码 / 公式围栏中的一行（可能在引用 / 剧透围栏中）
    private static MdLine ClassifyLine(string text, List<(int Start, int End)> raw, int index,
        MdLineKind? prevKind, MdLineKind? lastNonBlank, ref int tableSeparatorAt, ref bool inTable)
    {
        var (start, end) = raw[index];
        var line = text[start..end];
        if (string.IsNullOrWhiteSpace(line))
        {
            inTable = false;
            return new MdLine(start, end, MdLineKind.Blank) { ContentStart = start };
        }

        // 引用前缀；去掉所有 > 和空格后为空的行不是引用
        var prefixes = new List<MdPrefix>();
        var rest = 0;
        if (line.Any(c => c != '>' && c != ' '))
        {
            while (QuotePattern().Match(line, rest, line.Length - rest) is { Success: true } qm && qm.Index == rest)
            {
                prefixes.Add(new MdPrefix(MdPrefixKind.Quote, start + rest, start + rest + qm.Length));
                rest += qm.Length;
            }
        }
        var depth = prefixes.Count;
        var body = line[rest..];
        MdLine Make(MdLineKind kind) =>
            new(start, end, kind) { Prefixes = prefixes, ContentStart = prefixes.Count > 0 ? prefixes[^1].End : start, QuoteDepth = depth };

        if (tableSeparatorAt == index)
        {
            tableSeparatorAt = -1;
            return Make(MdLineKind.TableSeparator);
        }
        if (inTable)
        {
            if (body.Contains('|'))
                return Make(MdLineKind.TableRow);
            inTable = false;
        }

        if ((prevKind is null or MdLineKind.Blank or MdLineKind.IndentedCode)
            && (body.StartsWith("    ", StringComparison.Ordinal) || body.StartsWith('\t'))
            && (prevKind == MdLineKind.IndentedCode || lastNonBlank != MdLineKind.ListItem))
            return Make(MdLineKind.IndentedCode);

        if (HeadingPattern().Match(body) is { Success: true } hm)
        {
            prefixes.Add(new MdPrefix(MdPrefixKind.Heading, start + rest, start + rest + hm.Length));
            return Make(MdLineKind.Heading) with { HeadingLevel = hm.Groups["hashes"].Length };
        }

        if (RulePattern().IsMatch(body))
            return Make(MdLineKind.Rule);

        if (ListPattern().Match(body) is { Success: true } lm)
        {
            var marker = lm.Groups["marker"];
            var kind = char.IsDigit(marker.Value[0]) ? MdPrefixKind.Ordered : MdPrefixKind.Bullet;
            var space = lm.Groups["space"];
            prefixes.Add(new MdPrefix(kind, start + rest + marker.Index, start + rest + space.Index + space.Length));
            return Make(MdLineKind.ListItem);
        }

        if (body.Contains('|') && index + 1 < raw.Count)
        {
            var next = text[raw[index + 1].Start..raw[index + 1].End];
            var nextBody = next[QuotePrefixLength(next)..];
            if (TableSeparatorPattern().IsMatch(nextBody) && nextBody.Contains('|'))
            {
                tableSeparatorAt = index + 1;
                inTable = true;
                return Make(MdLineKind.TableRow);
            }
        }

        return Make(MdLineKind.Paragraph);
    }

    private static int QuotePrefixLength(string line)
    {
        var rest = 0;
        if (!line.Any(c => c != '>' && c != ' '))
            return 0;
        while (QuotePattern().Match(line, rest, line.Length - rest) is { Success: true } qm && qm.Index == rest)
            rest += qm.Length;
        return rest;
    }

    // 表格行按 | 切成单元格
    private static IEnumerable<(int Start, int End)> TableCells(string text, int start, int end)
    {
        var cellStart = start;
        foreach (var pipe in TablePipes(text, start, end))
        {
            if (pipe > cellStart)
                yield return (cellStart, pipe);
            cellStart = pipe + 1;
        }
        if (end > cellStart)
            yield return (cellStart, end);
    }

    /// <summary>表格行中分隔单元格的 | 的位置；行内代码中的 | 不算。</summary>
    public static IReadOnlyList<int> TablePipes(string text, int start, int end)
    {
        var pipes = new List<int>();
        var i = start;
        while (i < end)
        {
            var c = text[i];
            if (c == '`')
            {
                var run = 1;
                while (i + run < end && text[i + run] == '`')
                    run++;
                var close = FindTickRun(text, i + run, end, run);
                i = close >= 0 ? close + run : i + run;
                continue;
            }
            if (c == '|')
                pipes.Add(i);
            i++;
        }
        return pipes;
    }

    private static int FindTickRun(string text, int from, int end, int run)
    {
        for (var j = from; j < end; j++)
        {
            if (text[j] != '`')
                continue;
            var k = j;
            while (k < end && text[k] == '`')
                k++;
            if (k - j == run)
                return j;
            j = k - 1;
        }
        return -1;
    }

    // ───────────── 行内 ─────────────

    /// <summary>
    /// 移植 Python-Markdown 的行内处理：按优先级从高到低逐个模式匹配。原子元素整个遮盖，
    /// 加粗等只遮盖两侧符号，内容留给优先级更低的模式（服务器处理子节点时从下一个模式开始）。
    /// </summary>
    private static void ParseInline(string text, int start, int end, IMarkdownResolver resolver, List<MdSpan> spans)
    {
        if (end <= start)
            return;
        var masked = text.ToCharArray(start, end - start);

        Run(CodePattern(), m => new MdSpan(MdSpanKind.Code, m.Index, End(m), m.Groups["body"].Index, End(m.Groups["body"]), null), true);
        Run(StrongEmPattern(), m => Emphasis(MdSpanKind.StrongEm, m, 3), false);
        Run(UserMentionPattern(), m =>
        {
            var name = m.Groups["name"].Value;
            var silent = m.Groups["silent"].Length > 0;
            if (!Wildcards.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                var (plain, id) = SplitId(name);
                if (!resolver.HasUser(plain, id))
                    return null;
            }
            return Whole(MdSpanKind.UserMention, m, MentionText.Display(name, silent, isGroup: false));
        }, true);
        Run(MathPattern(), m =>
        {
            var s = new string(masked);
            if (m.Index > 0 && char.IsLetterOrDigit(s[m.Index - 1]) || End(m) < s.Length && char.IsLetterOrDigit(s[End(m)]))
                return null;
            return new MdSpan(MdSpanKind.Math, m.Index, End(m), m.Groups["body"].Index, End(m.Groups["body"]), null);
        }, true);
        Run(ChannelMessagePattern(), Channel, true);
        Run(ChannelTopicPattern(), Channel, true);
        Run(ChannelPattern(), Channel, true);
        Run(TimePattern(), m =>
            DateTimeOffset.TryParse(m.Groups["time"].Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
                ? Whole(MdSpanKind.Time, m, m.Groups["time"].Value)
                : null, true);
        Run(GroupMentionPattern(), m =>
            resolver.HasGroup(m.Groups["name"].Value)
                ? Whole(MdSpanKind.GroupMention, m, MentionText.Display(m.Groups["name"].Value, m.Groups["silent"].Length > 0, isGroup: true))
                : null, true);
        ScanLinks(masked, start, spans);
        Run(UrlPattern(), m =>
        {
            var url = TrimUrl(m.Groups["url"].Value);
            return url.Length == 0 ? null : new MdSpan(MdSpanKind.Url, m.Index, m.Index + url.Length, m.Index, m.Index + url.Length, url);
        }, true);
        Run(StrongPattern(), m => Emphasis(MdSpanKind.Strong, m, 2), false);
        Run(EmPattern(), m => Emphasis(MdSpanKind.Em, m, 1), false);
        Run(DelPattern(), m => Emphasis(MdSpanKind.Del, m, 2), false);
        Run(EmojiPattern(), m =>
            resolver.HasEmoji(m.Groups["name"].Value) ? Whole(MdSpanKind.Emoji, m, m.Groups["name"].Value) : null, true);
        return;

        // 对当前的遮盖文字逐个匹配；被拒绝的匹配从下一个字符继续找（与服务器一致：拒绝后文字留给其它模式）
        void Run(Regex pattern, Func<Match, MdSpan?> build, bool atomic)
        {
            var s = new string(masked);
            var pos = 0;
            while (pos <= s.Length && pattern.Match(s, pos) is { Success: true } m)
            {
                if (build(m) is not { } span)
                {
                    pos = m.Index + 1;
                    continue;
                }
                if (atomic)
                {
                    masked.AsSpan(span.Start, span.End - span.Start).Fill(Masked);
                }
                else
                {
                    masked.AsSpan(span.Start, span.ContentStart - span.Start).Fill(Masked);
                    masked.AsSpan(span.ContentEnd, span.End - span.ContentEnd).Fill(Masked);
                }
                spans.Add(Shift(span, start));
                s = new string(masked);
                pos = span.End;
            }
        }

        MdSpan? Channel(Match m)
        {
            var stream = m.Groups["stream"].Value;
            if (!resolver.HasChannel(stream))
                return null;
            var data = m.Groups["topic"].Success ? $"#{stream} > {m.Groups["topic"].Value}" : $"#{stream}";
            return Whole(MdSpanKind.ChannelLink, m, data);
        }
    }

    private static int End(Group g) => g.Index + g.Length;

    private static MdSpan Whole(MdSpanKind kind, Match m, string? data) => new(kind, m.Index, End(m), m.Index, End(m), data);

    private static MdSpan Emphasis(MdSpanKind kind, Match m, int marker) =>
        new(kind, m.Index, End(m), m.Index + marker, End(m) - marker, null);

    private static MdSpan Shift(MdSpan s, int by) =>
        s with { Start = s.Start + by, End = s.End + by, ContentStart = s.ContentStart + by, ContentEnd = s.ContentEnd + by };

    private static (string Name, long? Id) SplitId(string name)
    {
        var bar = name.LastIndexOf('|');
        if (bar > 0 && long.TryParse(name.AsSpan(bar + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var id))
            return (name[..bar], id);
        return (name, null);
    }

    // 去掉网址结尾的标点与不配对的右括号
    private static string TrimUrl(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?'\"".Contains(last))
                url = url[..^1];
            else if (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    /// <summary>
    /// 链接 <c>[文字](地址)</c> 与图片 <c>![文字](地址)</c>：方括号可以嵌套，地址可以用 &lt;&gt; 包围或含配对的圆括号。
    /// 上传的图片为 UploadImage，其它上传文件（视频等，有文字且不是 <c>![…]</c>）为 UploadFile，
    /// <c>[Uploading 名字…]()</c> 为 UploadPlaceholder。链接文字中不再识别其它格式。
    /// </summary>
    private static void ScanLinks(char[] masked, int offset, List<MdSpan> spans)
    {
        var n = masked.Length;
        var i = 0;
        while (i < n)
        {
            if (masked[i] != '[')
            {
                i++;
                continue;
            }
            var close = -1;
            var depth = 0;
            for (var j = i; j < n; j++)
            {
                if (masked[j] == '[')
                    depth++;
                else if (masked[j] == ']' && --depth == 0)
                {
                    close = j;
                    break;
                }
            }
            if (close < 0 || close + 1 >= n || masked[close + 1] != '(')
            {
                i++;
                continue;
            }

            var targetStart = close + 2;
            int targetEnd, end;
            if (targetStart < n && masked[targetStart] == '<')
            {
                var gt = Array.IndexOf(masked, '>', targetStart);
                if (gt < 0 || gt + 1 >= n || masked[gt + 1] != ')')
                {
                    i++;
                    continue;
                }
                targetEnd = gt + 1;
                end = gt + 2;
            }
            else
            {
                var paren = 1;
                var j = targetStart;
                for (; j < n; j++)
                {
                    if (masked[j] == '(')
                        paren++;
                    else if (masked[j] == ')' && --paren == 0)
                        break;
                }
                if (j >= n)
                {
                    i++;
                    continue;
                }
                targetEnd = j;
                end = j + 1;
            }

            var target = new string(masked, targetStart, targetEnd - targetStart).Trim();
            if (target.StartsWith('<'))
                target = target.Trim('<', '>');
            var space = target.IndexOfAny([' ', '\t']);
            var address = space >= 0 ? target[..space] : target;
            var label = new string(masked, i + 1, close - i - 1);
            var isImage = i > 0 && masked[i - 1] == '!';
            var start = isImage ? i - 1 : i;

            MdSpan span;
            if (address.Length == 0 && label.StartsWith("Uploading ", StringComparison.Ordinal) && label.EndsWith('…'))
                span = new MdSpan(MdSpanKind.UploadPlaceholder, start, end, start, end, label["Uploading ".Length..^1]);
            else if (ZulipHtml.NormalizeUploadPath(address) is { } path && ZulipHtml.IsImagePath(path))
                span = new MdSpan(MdSpanKind.UploadImage, start, end, start, end, address);
            else if (!isImage && !string.IsNullOrWhiteSpace(label) && ZulipHtml.NormalizeUploadPath(address) is not null)
                span = new MdSpan(MdSpanKind.UploadFile, start, end, i + 1, close, address);
            else if (string.IsNullOrWhiteSpace(label))
                span = new MdSpan(MdSpanKind.Link, start, end, i + 1, i + 1, address);
            else
                span = new MdSpan(MdSpanKind.Link, start, end, i + 1, close, address);

            masked.AsSpan(start, end - start).Fill(Masked);
            spans.Add(Shift(span, offset));
            i = end;
        }
    }

    // ───────────── 预览 ─────────────

    /// <summary>
    /// 草稿的单行预览，与 <see cref="ZulipHtml.ToPreview"/> 对发送后消息的规则相同：去掉引用；代码块、公式、剧透、
    /// 图片、视频、文件替换为 [代码]、[公式]、[剧透]、[图片]、[视频]、[文件]名称；提及、频道、时间为显示文字；
    /// 列表项前加 “•” 或序号；去掉格式符号；连续空白合并为一个空格。<paramref name="emojiText"/> 返回 Unicode 表情的文字，返回 null 时显示 :名称:。
    /// </summary>
    public static string ToPreview(string text, IMarkdownResolver resolver, Func<string, string?> emojiText)
    {
        var model = Parse(text, resolver);
        var sb = new StringBuilder();
        // 外层的围栏；剧透围栏中的内容整个显示为 [剧透]
        var fences = new List<MdFenceKind>();
        MdLineKind? prevKind = null;
        foreach (var line in model.Lines)
        {
            var inSpoiler = fences.Contains(MdFenceKind.Spoiler);
            if (line.Kind == MdLineKind.FenceOpen)
                fences.Add(line.Fence ?? MdFenceKind.Code);
            else if (line.Kind == MdLineKind.FenceClose && fences.Count > 0)
                fences.RemoveAt(fences.Count - 1);
            var kind = prevKind;
            prevKind = line.Kind;
            if (inSpoiler || line.QuoteDepth > 0)
                continue;

            switch (line.Kind)
            {
                case MdLineKind.FenceOpen:
                    sb.Append(line.Fence switch
                    {
                        MdFenceKind.Math => " [公式] ",
                        MdFenceKind.Spoiler => " [折叠] ",
                        _ => " [代码] ",
                    });
                    break;
                // 连续的缩进代码是一个代码块
                case MdLineKind.IndentedCode when kind != MdLineKind.IndentedCode:
                    sb.Append(" [代码] ");
                    break;
                case MdLineKind.Paragraph or MdLineKind.Heading or MdLineKind.ListItem or MdLineKind.TableRow:
                    sb.Append(' ');
                    // 列表项前加圆点 “•” 或序号 “1.”（与输入框中的显示相同）
                    if (line.Kind == MdLineKind.ListItem && line.Prefixes[^1] is var marker)
                        sb.Append(marker.Kind == MdPrefixKind.Bullet ? "•" : text[marker.Start..marker.End].Trim()).Append(' ');
                    AppendInline(sb, text, model, line, preview: true, emojiText);
                    sb.Append(' ');
                    break;
            }
        }
        return PreviewWhitespacePattern().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// 输入框中显示的文字（复制到其它程序用）：保留换行；去掉格式符号；引用每行前加 “&gt; ”，列表圆点为 “• ”；
    /// 代码块、公式块保留原文；提及、频道、时间、表情与输入框中的显示相同；链接为“文字 (地址)”，图片为 [图片]。
    /// </summary>
    public static string ToPlainText(string text, IMarkdownResolver resolver, Func<string, string?> emojiText)
    {
        var model = Parse(text, resolver);
        var lines = new List<string>();
        var sb = new StringBuilder();
        foreach (var line in model.Lines)
        {
            sb.Clear();
            sb.Append(string.Concat(Enumerable.Repeat("> ", line.QuoteDepth)));
            switch (line.Kind)
            {
                // 引用、代码、公式围栏的符号行不显示；剧透显示标题
                case MdLineKind.FenceOpen when line is { Fence: MdFenceKind.Spoiler, FenceInfo.Length: > 0 }:
                    sb.Append(line.FenceInfo);
                    break;
                case MdLineKind.FenceOpen or MdLineKind.FenceClose or MdLineKind.Rule or MdLineKind.TableSeparator:
                    continue;
                case MdLineKind.FenceBody:
                    sb.Append(text, line.ContentStart, line.End - line.ContentStart);
                    break;
                case MdLineKind.IndentedCode:
                    var code = text[line.ContentStart..line.End];
                    sb.Append(code.StartsWith('\t') ? code[1..] : code[Math.Min(4, code.Length)..]);
                    break;
                case MdLineKind.Blank:
                    sb.Clear();
                    break;
                case MdLineKind.ListItem:
                    // 列表标记前的缩进（嵌套列表）保留
                    var marker = line.Prefixes[^1];
                    var indentStart = line.Prefixes.Count > 1 ? line.Prefixes[^2].End : line.Start;
                    sb.Append(text, indentStart, marker.Start - indentStart)
                        .Append(marker.Kind == MdPrefixKind.Bullet ? "• " : text[marker.Start..marker.End]);
                    AppendInline(sb, text, model, line, preview: false, emojiText);
                    break;
                default:
                    AppendInline(sb, text, model, line, preview: false, emojiText);
                    break;
            }
            lines.Add(sb.ToString());
        }
        return string.Join('\n', lines);
    }

    // 一行的行内内容
    private static void AppendInline(StringBuilder sb, string text, MarkdownModel model, MdLine line, bool preview,
        Func<string, string?> emojiText)
    {
        var spans = model.Spans.Where(s => s.Start >= line.ContentStart && s.End <= line.End).ToList();
        AppendInline(sb, text, spans, line.ContentStart, line.End, preview && line.Kind == MdLineKind.TableRow, preview, emojiText);
    }

    // [from, to) 的显示文字。spans 按 Start 升序；加粗等只去掉符号，内容中可能还有其它元素
    private static void AppendInline(StringBuilder sb, string text, List<MdSpan> spans, int from, int to, bool tableRow,
        bool preview, Func<string, string?> emojiText)
    {
        var pos = from;
        foreach (var s in spans)
        {
            if (s.Start < pos || s.End > to)
                continue;
            AppendPlain(sb, text, pos, s.Start, tableRow);
            if (SpanText(text, s, preview, emojiText) is { } replacement)
                sb.Append(replacement);
            // 没有符号的元素（网址）：内容就是它自己，不再递归
            else if (s.ContentStart == s.Start && s.ContentEnd == s.End)
                AppendPlain(sb, text, s.Start, s.End, tableRow);
            else
                AppendInline(sb, text, spans, s.ContentStart, s.ContentEnd, tableRow, preview, emojiText);
            pos = s.End;
        }
        AppendPlain(sb, text, pos, to, tableRow);
    }

    // 表格单元格之间的 | 换成空格
    private static void AppendPlain(StringBuilder sb, string text, int from, int to, bool tableRow)
    {
        if (to <= from)
            return;
        if (tableRow)
            sb.Append(text, from, to - from).Replace('|', ' ', sb.Length - (to - from), to - from);
        else
            sb.Append(text, from, to - from);
    }

    // 元素的替换文字；返回 null 表示只去掉符号、显示内容。预览与 ZulipHtml 一样，替换文字两侧加空格
    private static string? SpanText(string text, MdSpan s, bool preview, Func<string, string?> emojiText)
    {
        var label = text[s.ContentStart..s.ContentEnd];
        var result = s.Kind switch
        {
            MdSpanKind.Math => preview ? "[公式]" : text[s.Start..s.End],
            MdSpanKind.UploadImage => "[图片]",
            MdSpanKind.UploadPlaceholder => "",
            MdSpanKind.UserMention or MdSpanKind.GroupMention or MdSpanKind.ChannelLink => s.Data ?? "",
            MdSpanKind.Time => TimeLabel(s.Data),
            MdSpanKind.Emoji => s.Data is { } name ? emojiText(name) ?? $":{name}:" : "",
            // 外链图片 ![…](…)
            MdSpanKind.Link when text[s.Start] == '!' => "[图片]",
            MdSpanKind.Link or MdSpanKind.UploadFile when ZulipHtml.NormalizeUploadPath(s.Data) is { } path =>
                preview ? ZulipHtml.IsVideoPath(path) ? "[视频]" : $"[文件]{label.Trim()}" : label,
            // [](地址) 显示地址
            MdSpanKind.Link when label.Length == 0 || label == s.Data => s.Data ?? "",
            MdSpanKind.Link when !preview => $"{label} ({s.Data})",
            _ => null,
        };
        // 链接文字（预览）与没有特殊处理的元素：显示内容；[](地址) 的地址不加空格
        if (result is null || !preview || s.Kind == MdSpanKind.Link && !result.StartsWith('['))
            return result;
        return $" {result} ";
    }

    // 与消息中 <time> 的显示相同
    private static string TimeLabel(string? time) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : time ?? "";

    // ───────────── 正则（来自 zerver/lib/markdown） ─────────────

    [GeneratedRegex(@"\s+")]
    private static partial Regex PreviewWhitespacePattern();

    [GeneratedRegex(@"^(?<fence>~{3,}|`{3,})[ ]*(?:\{?\.?(?<lang>[a-zA-Z0-9_+\-./#]+)[ ]*(?<header>[^ ~`][^~`]*)?\}?)?$")]
    private static partial Regex FencePattern();

    [GeneratedRegex(@"^(?<hashes>#{1,6})[ \t]")]
    private static partial Regex HeadingPattern();

    [GeneratedRegex(@"\G {0,3}> ?")]
    private static partial Regex QuotePattern();

    [GeneratedRegex(@"^(?<indent> *)(?<marker>[*+\-]|\d+\.)(?<space> +)\S")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"^ {0,3}(?:(?:-[ ]*){3,}|(?:\*[ ]*){3,}|(?:_[ ]*){3,})$")]
    private static partial Regex RulePattern();

    [GeneratedRegex(@"^ *\|? *:?-+:? *(?:\| *:?-+:? *)+\|? *$")]
    private static partial Regex TableSeparatorPattern();

    [GeneratedRegex(@"(?<!`)(?<tick>`+)(?<body>.+?)(?<!`)\k<tick>(?!`)")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"(\*\*\*)(?!\s)([^\*^\n]+)(?<!\s)\*\*\*")]
    private static partial Regex StrongEmPattern();

    [GeneratedRegex("""(?<![^\s'"\(\{\[\/<])@(?<silent>_?)\*\*(?<name>[^\*]+)\*\*""")]
    private static partial Regex UserMentionPattern();

    [GeneratedRegex(@"(?<!\$)\$\$(?<body>[^\n_$](?:\\\$|[^$\n])*)\$\$(?!\$)")]
    private static partial Regex MathPattern();

    [GeneratedRegex(@"#\*\*(?<stream>[^\*>]+)>(?<topic>[^\*]*)@(?<msg>\d+)\*\*")]
    private static partial Regex ChannelMessagePattern();

    [GeneratedRegex(@"#\*\*(?<stream>[^\*>]+)>(?<topic>[^\*]*)\*\*")]
    private static partial Regex ChannelTopicPattern();

    [GeneratedRegex(@"#\*\*(?<stream>[^\*]+)\*\*")]
    private static partial Regex ChannelPattern();

    [GeneratedRegex(@"<time:(?<time>[^>]*?)>")]
    private static partial Regex TimePattern();

    [GeneratedRegex("""(?<![^\s'"\(\{\[\/<])@(?<silent>_?)\*(?<name>[^\*]+)\*""")]
    private static partial Regex GroupMentionPattern();

    [GeneratedRegex("""(?<=^|[\s'"(,:<\u0080-￿])(?<url>(?:https?|ftp)://[^\s<>\u0002]+)""")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"(\*\*)([^\n]+?)\*\*")]
    private static partial Regex StrongPattern();

    [GeneratedRegex(@"(\*)(?!\s)([^\*^\n]+)(?<!\s)\*")]
    private static partial Regex EmPattern();

    [GeneratedRegex(@"(?<!~)(~~)([^~\n]+?)(~~)(?!~)")]
    private static partial Regex DelPattern();

    [GeneratedRegex(@":(?<name>[\w\-\+]+):")]
    private static partial Regex EmojiPattern();
}
