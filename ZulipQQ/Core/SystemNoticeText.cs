using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace ZulipQQ.Core;

/// <summary>
/// Notification Bot 在话题中的事件提示（话题移动、消息移动、标记为已解决）翻译为中文。
/// 服务器按组织的默认语言发送，英文组织收到的是英文；句中的提及、频道链接等元素原样保留。
/// 不认识的句子原样返回。
/// </summary>
public static partial class SystemNoticeText
{
    // 句子中的元素用 ⟦序号⟧ 代替后再匹配（见 zerver/actions/message_edit.py）
    private static readonly (Regex Pattern, string Text)[] Rules =
    [
        (TopicMovedHere(), "{user} 将此话题从 {from} 移到了这里。"),
        (TopicMovedTo(), "{user} 将此话题移到了 {to}。"),
        (MessagesMovedHere(), "{user} 从 {from} 移来了 {count}。"),
        (MessagesMovedTo(), "{user} 将 {count} 移到了 {to}。"),
        (MarkedResolved(), "{user} 将此话题标记为已解决。"),
        (MarkedUnresolved(), "{user} 将此话题标记为未解决。"),
    ];

    /// <summary>翻译提示的 HTML；只翻译只有一个段落、且句子认识的提示。</summary>
    public static string Localize(string html)
    {
        var root = ZulipHtml.ParseFragment(html);
        if (root.Children.Length != 1 || root.Children[0] is not { LocalName: "p" } p)
            return html;

        var elements = new List<IElement>();
        var sentence = new StringBuilder();
        foreach (var node in p.ChildNodes)
        {
            if (node is IElement e)
            {
                sentence.Append('⟦').Append(elements.Count).Append('⟧');
                elements.Add(e);
            }
            else
            {
                sentence.Append(node.TextContent);
            }
        }
        var text = Whitespace().Replace(sentence.ToString(), " ").Trim();

        foreach (var (pattern, template) in Rules)
        {
            if (pattern.Match(text) is not { Success: true } m)
                continue;
            var result = PlaceholderPattern().Replace(template, t => Part(m.Groups[t.Groups[1].Value].Value, elements));
            return $"<p>{result}</p>";
        }
        return html;
    }

    // 占位的元素换回原来的 HTML；消息条数（“A message”“3 messages”，可能是指向消息的链接）译为“N 条消息”
    private static string Part(string value, List<IElement> elements)
    {
        if (ElementPattern().Match(value) is { Success: true } em)
        {
            var e = elements[int.Parse(em.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
            if (CountText(e.TextContent.Trim()) is { } count)
                e.TextContent = count;
            return e.OuterHtml;
        }
        return System.Net.WebUtility.HtmlEncode(CountText(value) ?? value);
    }

    private static string? CountText(string value)
    {
        if (value == "A message")
            return "1 条消息";
        var m = CountPattern().Match(value);
        return m.Success ? $"{m.Groups[1].Value} 条消息" : null;
    }

    [GeneratedRegex(@"\{(\w+)\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex(@"^⟦(\d+)⟧$")]
    private static partial Regex ElementPattern();

    [GeneratedRegex(@"^(\d+) messages$")]
    private static partial Regex CountPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"^This topic was moved here from (?<from>⟦\d+⟧) by (?<user>⟦\d+⟧)\.$")]
    private static partial Regex TopicMovedHere();

    [GeneratedRegex(@"^This topic was moved to (?<to>⟦\d+⟧) by (?<user>⟦\d+⟧)\.$")]
    private static partial Regex TopicMovedTo();

    [GeneratedRegex(@"^(?<count>A message|\d+ messages|⟦\d+⟧) (?:was|were) moved here from (?<from>⟦\d+⟧) by (?<user>⟦\d+⟧)\.$")]
    private static partial Regex MessagesMovedHere();

    [GeneratedRegex(@"^(?<count>A message|\d+ messages|⟦\d+⟧) (?:was|were) moved from this topic to (?<to>⟦\d+⟧) by (?<user>⟦\d+⟧)\.$")]
    private static partial Regex MessagesMovedTo();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) has marked this topic as resolved\.$")]
    private static partial Regex MarkedResolved();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) has marked this topic as unresolved\.$")]
    private static partial Regex MarkedUnresolved();
}
