using System;

namespace ZulipQQ.Core;

/// <summary>输入框中 @ 补全的文字处理。</summary>
public static class MentionText
{
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

    /// <summary>用 <paramref name="insertion"/> 替换 <c>[start, caret)</c>，返回新文字与新的光标位置。</summary>
    public static (string Text, int Caret) Replace(string text, int start, int caret, string insertion) =>
        (string.Concat(text.AsSpan(0, start), insertion, text.AsSpan(caret)), start + insertion.Length);

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
}
