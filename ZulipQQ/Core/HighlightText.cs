using System;
using System.Collections.Generic;

namespace ZulipQQ.Core;

/// <summary>搜索结果中把命中的部分标出来：按查询词（不区分大小写）把文字切成普通段和命中段。</summary>
public static class HighlightText
{
    public static IReadOnlyList<(string Text, bool IsMatch)> Split(string text, string? query)
    {
        var result = new List<(string, bool)>();
        if (string.IsNullOrEmpty(query))
        {
            if (text.Length > 0)
                result.Add((text, false));
            return result;
        }

        var start = 0;
        while (start < text.Length)
        {
            var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
                break;
            if (index > start)
                result.Add((text[start..index], false));
            result.Add((text.Substring(index, query.Length), true));
            start = index + query.Length;
        }
        if (start < text.Length)
            result.Add((text[start..], false));
        return result;
    }
}
