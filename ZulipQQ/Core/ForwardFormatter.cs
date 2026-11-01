using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ZulipQQ.Core;

/// <summary>合并转发中的一条消息：发送者、发送时间（本地时间）、Markdown 原文。</summary>
public sealed record ForwardedMessage(string SenderName, DateTime Time, string Raw);

/// <summary>合并转发：把多条消息拼成“聊天记录”格式的 Markdown。</summary>
public static class ForwardFormatter
{
    public const string Header = "**聊天记录**\n";

    private const string Ellipsis = "…";

    /// <summary>
    /// 每条消息为 <c>**发送者** yyyy/MM/dd HH:mm</c> 加 quote 代码块，开头是 <see cref="Header"/>。
    /// 超过 <paramref name="maxLength"/> 时在消息之间拆成多条，每条都以标题开头；
    /// 单条消息加上标题仍然超长时截断原文，末尾加“…”。<paramref name="maxLength"/> ≤ 0 表示不限制。
    /// </summary>
    public static IReadOnlyList<string> Merge(IReadOnlyList<ForwardedMessage> messages, int maxLength)
    {
        var result = new List<string>();
        var current = new StringBuilder(Header);
        var hasBlock = false;
        foreach (var m in messages)
        {
            var block = Block(m, null);
            if (maxLength > 0 && Header.Length + block.Length > maxLength)
                block = Block(m, maxLength - Header.Length);
            if (hasBlock && maxLength > 0 && current.Length + block.Length > maxLength)
            {
                result.Add(current.ToString().TrimEnd('\n'));
                current.Clear().Append(Header);
            }
            current.Append(block);
            hasBlock = true;
        }
        if (hasBlock)
            result.Add(current.ToString().TrimEnd('\n'));
        return result;
    }

    /// <summary>一条消息的块；<paramref name="limit"/> 不为 null 时截断原文，使整块不超过该长度。</summary>
    private static string Block(ForwardedMessage m, int? limit)
    {
        var title = $"**{m.SenderName}** {m.Time.ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)}\n";
        var raw = m.Raw.TrimEnd();
        // 原文中含有 ``` 时加长围栏，避免提前结束（与引用的规则相同）
        var fence = "```";
        while (raw.Contains(fence, StringComparison.Ordinal))
            fence += "`";
        var open = fence + "quote\n";
        var close = "\n" + fence + "\n";
        if (limit is { } max)
        {
            var room = max - title.Length - open.Length - close.Length - Ellipsis.Length;
            raw = room > 0 ? raw[..Math.Min(raw.Length, room)] + Ellipsis : Ellipsis;
        }
        return title + open + raw + close;
    }
}
