using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ZulipQQ.Core;

/// <summary>
/// 正文中一段引用消息（由“引用”插入，格式见 ComposerViewModel.QuoteMarkdown）：
/// 第 <see cref="HeaderLine"/> 行是“@_**发送者|id** [发言道](消息链接)：”，下一行起是 quote 围栏，到第 <see cref="CloseLine"/> 行结束。
/// [<see cref="Start"/>, <see cref="End"/>) 为整段（不含最后的换行），[<see cref="BodyStart"/>, <see cref="BodyEnd"/>) 为被引用的原文。
/// </summary>
public sealed record QuoteBlock(
    int HeaderLine, int CloseLine, int Start, int End, int BodyStart, int BodyEnd,
    string SenderName, long? SenderId, string Link, long? MessageId);

public static partial class QuoteBlocks
{
    /// <summary>
    /// 找出 <paramref name="text"/> 中所有完整的引用消息（<paramref name="model"/> 为它的解析结果）。
    /// 只认最外层的：引用行后面紧跟 quote 围栏，并且围栏已经结束；还没有写完的不算。
    /// </summary>
    public static IReadOnlyList<QuoteBlock> Find(MarkdownModel model, string text)
    {
        var result = new List<QuoteBlock>();
        var lines = model.Lines;
        for (var i = 0; i + 1 < lines.Count; i++)
        {
            var header = lines[i];
            var open = lines[i + 1];
            if (header.Kind != MdLineKind.Paragraph || header.QuoteDepth != 0
                || open is not { Kind: MdLineKind.FenceOpen, Fence: MdFenceKind.Quote, QuoteDepth: 1 }
                || HeaderPattern().Match(text, header.Start, header.End - header.Start) is not { Success: true } m)
                continue;
            // 与围栏开始配对的结束行：中间可以有嵌套的围栏
            var depth = 1;
            var close = -1;
            for (var k = i + 2; k < lines.Count && close < 0; k++)
            {
                if (lines[k].Kind == MdLineKind.FenceOpen)
                    depth++;
                else if (lines[k].Kind == MdLineKind.FenceClose && --depth == 0)
                    close = k;
            }
            if (close < 0)
                continue;

            var link = m.Groups["link"].Value;
            var bodyStart = close > i + 2 ? lines[i + 2].Start : lines[close].Start;
            var bodyEnd = close > i + 2 ? lines[close - 1].End : bodyStart;
            result.Add(new QuoteBlock(i, close, header.Start, lines[close].End, bodyStart, bodyEnd,
                m.Groups["name"].Value,
                m.Groups["id"].Success ? long.Parse(m.Groups["id"].Value, CultureInfo.InvariantCulture) : null,
                link,
                NearPattern().Match(link) is { Success: true } near ? long.Parse(near.Groups[1].Value, CultureInfo.InvariantCulture) : null));
            i = close;
        }
        return result;
    }

    /// <summary>除了引用消息只有空白：只引用、没有写自己的内容（不能发送）。</summary>
    public static bool HasOnlyQuotes(string text)
    {
        var quotes = Find(ZulipMarkdown.Parse(text, NullResolver.Instance), text);
        if (quotes.Count == 0)
            return false;
        var rest = 0;
        foreach (var quote in quotes)
        {
            if (!string.IsNullOrWhiteSpace(text[rest..quote.Start]))
                return false;
            rest = quote.End;
        }
        return string.IsNullOrWhiteSpace(text[rest..]);
    }

    // 只需要行的划分，提及、表情等是否存在无关
    private sealed class NullResolver : IMarkdownResolver
    {
        public static readonly NullResolver Instance = new();
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }

    // 简体中文译文“发言道”与英文原文“said”都认；冒号可以是全角或半角
    [GeneratedRegex(@"\G@_\*\*(?<name>[^*|\n]+?)(?:\|(?<id>\d+))?\*\* \[(?:发言道|said)\]\((?<link>[^)\s]+)\)[：:][ \t]*$", RegexOptions.Multiline)]
    private static partial Regex HeaderPattern();

    // 消息链接中的消息 id：…/near/603 或 …/with/603
    [GeneratedRegex(@"/(?:near|with)/(\d+)")]
    private static partial Regex NearPattern();
}
