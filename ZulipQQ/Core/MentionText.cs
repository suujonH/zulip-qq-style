using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

/// <summary>输入框中的一个提及：原文 <c>[Start, End)</c> 为 <see cref="Markdown"/>。</summary>
public readonly record struct MentionSpan(int Start, int Length, string Markdown)
{
    public int End => Start + Length;
}

/// <summary>输入框中 @ 补全与提及的文字处理。</summary>
public static partial class MentionText
{
    /// <summary>遮盖提及文字用的字符：不是 <c>@</c> 也不是空白，提及中的 @ 不会再触发补全。</summary>
    public const char MaskChar = '￼';

    /// <summary>
    /// 光标前是否正在输入提及：从光标向前找到 <c>@</c>，它位于开头或空白字符之后，且中间没有空白。
    /// <paramref name="start"/> 为 <c>@</c> 的位置，<paramref name="query"/> 为 <c>@</c> 到光标之间的文字（可以为空）。
    /// 已经补全的提及（<c>@**</c>、<c>@*</c>、<c>@_</c>）不再触发。
    /// </summary>
    public static bool TryGetQuery(string text, int caret, out int start, out string query)
    {
        start = -1;
        query = "";
        if (caret < 1 || caret > text.Length)
            return false;
        for (var i = caret - 1; i >= 0; i--)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
                return false;
            if (c != '@')
                continue;
            if (i > 0 && !char.IsWhiteSpace(text[i - 1]))
                return false;
            var q = text[(i + 1)..caret];
            if (q.StartsWith('*') || q.StartsWith('_'))
                return false;
            start = i;
            query = q;
            return true;
        }
        return false;
    }

    /// <summary>
    /// 匹配程度：任一字段以关键字开头为 0，包含关键字为 1，都不匹配为 -1（忽略大小写）。关键字为空时为 0。
    /// </summary>
    public static int MatchRank(string query, params string?[] fields)
    {
        if (query.Length == 0)
            return 0;
        var rank = -1;
        foreach (var f in fields)
        {
            if (string.IsNullOrEmpty(f))
                continue;
            if (f.StartsWith(query, StringComparison.OrdinalIgnoreCase))
                return 0;
            if (f.Contains(query, StringComparison.OrdinalIgnoreCase))
                rank = 1;
        }
        return rank;
    }

    /// <summary>提及的文字换成 <see cref="MaskChar"/>，用于判断补全（提及中的 @ 不触发补全）。</summary>
    public static string Mask(string text, IReadOnlyList<MentionSpan> spans)
    {
        if (spans.Count == 0)
            return text;
        var chars = text.ToCharArray();
        foreach (var s in spans.Where(s => s.End <= chars.Length))
            chars.AsSpan(s.Start, s.Length).Fill(MaskChar);
        return new string(chars);
    }

    /// <summary>
    /// 提及的显示文字：<paramref name="name"/> 为 <c>@**…**</c>（或用户组 <c>@*…*</c>）中的名字。
    /// <c>名字|id</c> 取名字，all、everyone、channel、stream 为“全体成员”，topic 为“话题参与者”；静默提及没有 @。
    /// </summary>
    public static string Display(string name, bool silent, bool isGroup)
    {
        var at = silent ? "" : "@";
        if (isGroup)
            return at + name;
        var bar = name.LastIndexOf('|');
        if (bar > 0)
            return at + name[..bar];
        return name switch
        {
            "all" or "everyone" or "channel" or "stream" => at + "全体成员",
            "topic" => at + "话题参与者",
            _ => at + name,
        };
    }

    /// <summary>
    /// 去掉 Markdown 中的 @全体成员（all、everyone、channel、stream；<paramref name="topic"/> 为 true 时为 @话题参与者），
    /// 连同后面的一个空格。静默提及（<c>@_</c>）不通知，保留。
    /// </summary>
    public static string RemoveWildcards(string markdown, bool topic) =>
        (topic ? TopicWildcardPattern() : StreamWildcardPattern()).Replace(markdown, "");

    [GeneratedRegex("""(?<![^\s'"({\[/<])@\*\*(?:all|everyone|channel|stream)\*\* ?""")]
    private static partial Regex StreamWildcardPattern();

    [GeneratedRegex("""(?<![^\s'"({\[/<])@\*\*topic\*\* ?""")]
    private static partial Regex TopicWildcardPattern();
}
