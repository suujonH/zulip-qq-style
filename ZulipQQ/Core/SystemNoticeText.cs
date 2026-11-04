using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;

namespace ZulipQQ.Core;

/// <summary>
/// Notification Bot 在话题中的事件提示（话题移动、消息移动、标记为已解决）与消息提醒翻译为中文。
/// 服务器按组织的默认语言发送，英文组织收到的是英文；句中的提及、频道链接等元素原样保留。
/// 不认识的句子原样返回。模板中 {name} 为句中对应的部分，{name=文字} 同时把该元素（链接）的文字换掉。
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

    // 消息提醒（见 zerver/lib/reminders.py）：第一段说明，第二段是被提醒消息的出处，之后是引用的原文
    private static readonly (Regex Pattern, string Text)[] ReminderRules =
    [
        (ReminderIntro(), "你为以下{kind}设置了提醒。{note}"),
        (SaidIn(), "{user} 在 {topic} 中{said=说}："),
        (SaidTo(), "{user} 对 {to} {said=说}："),
        (SentWidgetIn(), "{user} 在 {topic} 中{sent=发起}了{widget}。"),
        (SentWidgetTo(), "{user} 向 {to} {sent=发起}了{widget}。"),
        (SentSelfWidget(), "你给自己{sent=发起}了{widget}。"),
        (NoteToSelf(), "你给自己{said=写}了一条笔记："),
    ];

    /// <summary>翻译提示的 HTML；只翻译只有一个段落、且句子认识的提示。</summary>
    public static string Localize(string html)
    {
        var root = ZulipHtml.ParseFragment(html);
        if (root.Children.Length != 1 || root.Children[0] is not { LocalName: "p" } p)
            return html;
        return Translate(p, Rules) is { } result ? $"<p>{result}</p>" : html;
    }

    /// <summary>翻译消息提醒的 HTML：各段落中认识的句子译为中文，引用的原文等其它内容不变。</summary>
    public static string LocalizeReminder(string html)
    {
        var root = ZulipHtml.ParseFragment(html);
        var changed = false;
        foreach (var p in root.Children.Where(e => e.LocalName == "p").ToList())
        {
            if (Translate(p, ReminderRules) is not { } result)
                continue;
            p.InnerHtml = result;
            changed = true;
        }
        return changed ? root.InnerHtml : html;
    }

    // 段落的句子认识时返回译文（段落内部的 HTML），否则返回 null
    private static string? Translate(IElement p, (Regex Pattern, string Text)[] rules)
    {
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

        foreach (var (pattern, template) in rules)
        {
            if (pattern.Match(text) is not { Success: true } m)
                continue;
            return PlaceholderPattern().Replace(template,
                t => Part(m.Groups[t.Groups[1].Value].Value, t.Groups[2].Success ? t.Groups[2].Value : null, elements));
        }
        return null;
    }

    // 占位的元素换回原来的 HTML；消息条数（“A message”“3 messages”，可能是指向消息的链接）译为“N 条消息”。
    // 几个人（“A, B, and C”）译为“A、B 和 C”
    private static string Part(string value, string? linkText, List<IElement> elements)
    {
        if (ElementPattern().Match(value) is { Success: true } em)
        {
            var e = elements[int.Parse(em.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)];
            if (linkText is not null)
                e.TextContent = linkText;
            else if (CountText(e.TextContent.Trim()) is { } count)
                e.TextContent = count;
            return e.OuterHtml;
        }
        // 提醒的备注：一般是下面的引用段落，与说明在同一段落时原样放在“备注：”后
        if (value.StartsWith(" Note:", System.StringComparison.Ordinal))
        {
            var note = InlineElementPattern().Replace(System.Net.WebUtility.HtmlEncode(value[" Note:".Length..].Trim()),
                m => elements[int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)].OuterHtml);
            return "备注：" + note;
        }
        if (value.Contains('⟦'))
        {
            return ListPattern().Replace(value, m => m.Groups[1].Success
                ? Part(m.Value, null, elements)
                : m.Value.Contains("and") ? " 和 " : "、");
        }
        var translated = value switch
        {
            "message" => "消息",
            "direct message" => "私信",
            "poll" => "一个投票",
            "todo list" => "一个待办清单",
            _ => CountText(value) ?? value,
        };
        return System.Net.WebUtility.HtmlEncode(translated);
    }

    private static string? CountText(string value)
    {
        if (value == "A message")
            return "1 条消息";
        var m = CountPattern().Match(value);
        return m.Success ? $"{m.Groups[1].Value} 条消息" : null;
    }

    [GeneratedRegex(@"\{(\w+)(?:=([^}]*))?\}")]
    private static partial Regex PlaceholderPattern();

    // 人名列表中的元素或分隔符
    [GeneratedRegex(@"(⟦\d+⟧)|,? and |, ")]
    private static partial Regex ListPattern();

    [GeneratedRegex(@"⟦(\d+)⟧")]
    private static partial Regex InlineElementPattern();

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

    private const string Recipients = @"(?<to>⟦\d+⟧(?:(?:, |,? and )⟦\d+⟧)*)";

    [GeneratedRegex(@"^You requested a reminder for the following (?<kind>message|direct message)\.(?<note> Note:.*)?$", RegexOptions.Singleline)]
    private static partial Regex ReminderIntro();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) (?<said>⟦\d+⟧) in (?<topic>⟦\d+⟧):$")]
    private static partial Regex SaidIn();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) (?<said>⟦\d+⟧) to " + Recipients + ":$")]
    private static partial Regex SaidTo();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) (?<sent>⟦\d+⟧) a (?<widget>poll|todo list) in (?<topic>⟦\d+⟧)\.$")]
    private static partial Regex SentWidgetIn();

    [GeneratedRegex(@"^(?<user>⟦\d+⟧) (?<sent>⟦\d+⟧) a (?<widget>poll|todo list) to " + Recipients + @"\.$")]
    private static partial Regex SentWidgetTo();

    [GeneratedRegex(@"^You (?<sent>⟦\d+⟧) yourself a (?<widget>poll|todo list)\.$")]
    private static partial Regex SentSelfWidget();

    [GeneratedRegex(@"^You (?<said>⟦\d+⟧) a note to yourself:$")]
    private static partial Regex NoteToSelf();
}
