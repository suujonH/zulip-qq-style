using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace ZulipQQ.Core;

/// <summary>消息中的上传文件链接。</summary>
/// <param name="Path">以 <c>/user_uploads/</c> 开头的相对路径。</param>
public sealed record UploadLink(string Path, string FileName, bool IsImage);

/// <summary>消息中的一张内联图片：显示用的地址与原链接（原链接可能为空）。</summary>
public sealed record InlineImage(string Src, string Href)
{
    /// <summary>查看原图用的地址：本组织上传的图片为上传路径，其它为显示用的地址（外链经服务器代理）。</summary>
    public string Original => ZulipHtml.NormalizeUploadPath(Href) ?? Src;
}

/// <summary>
/// 把 Zulip 渲染的消息 HTML 转为纯文本，供会话列表预览、通知、复制和插件使用。
/// </summary>
public static partial class ZulipHtml
{
    private const string UploadsPrefix = "/user_uploads/";

    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    private static readonly HashSet<string> VideoExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".mp4", ".webm", ".mov" };

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "blockquote", "pre", "ul", "ol", "li", "table", "thead", "tbody", "tr", "td", "th",
        "h1", "h2", "h3", "h4", "h5", "h6", "hr", "br",
    };

    private static readonly HtmlParser Parser = new();

    /// <summary>
    /// 单行预览：去掉引用块，图片、文件、代码、公式等替换为 [图片]、[文件]名称、[代码]、[公式]，
    /// 连续空白合并为一个空格。
    /// </summary>
    public static string ToPreview(string html)
    {
        var root = Parse(html);
        var sb = new StringBuilder();
        var inlineImages = InlineImageHrefs(root);
        foreach (var child in root.ChildNodes)
            AppendPreview(child, sb, inlineImages);
        return CollapseWhitespace().Replace(sb.ToString(), " ").Trim();
    }

    /// <summary>
    /// 多行纯文本：段落、换行、列表项之间用换行分隔；引用块每行前加 “> ”；代码块保留原文。
    /// </summary>
    public static string ToPlainText(string html)
    {
        var root = Parse(html);
        var lines = new List<string>();
        var inlineImages = InlineImageHrefs(root);
        AppendPlainBlocks(root.ChildNodes, lines, inlineImages);
        return string.Join('\n', lines).Trim('\n');
    }

    /// <summary>
    /// 内容只有图片 / 视频预览和上传文件链接（聊天中不显示气泡背景）。
    /// </summary>
    public static bool IsAttachmentOnly(string html)
    {
        var root = Parse(html);
        var inlineImages = InlineImageHrefs(root);
        var any = false;
        foreach (var node in root.ChildNodes)
        {
            if (node is IText text)
            {
                if (!string.IsNullOrWhiteSpace(text.Data))
                    return false;
                continue;
            }
            if (node is not IElement e)
                continue;
            if (IsUploadPreview(e) || (e.LocalName == "p" && IsUploadOnlyParagraph(e)))
            {
                any = true;
                continue;
            }
            return false;
        }
        return any;
    }

    /// <summary>上传图片 / 视频的内联预览（<c>div.message_inline_image</c> 中指向 <c>/user_uploads/</c> 的链接）。</summary>
    public static bool IsUploadPreview(IElement e) =>
        e.ClassList.Contains("message_inline_image")
        && NormalizeUploadPath(e.QuerySelector("a[href]")?.GetAttribute("href")) is not null;

    /// <summary>段落中只有上传文件链接（以及空白和换行）。</summary>
    public static bool IsUploadOnlyParagraph(IElement p)
    {
        var any = false;
        foreach (var node in p.ChildNodes)
        {
            switch (node)
            {
                case IText t when string.IsNullOrWhiteSpace(t.Data):
                    continue;
                case IElement { LocalName: "br" }:
                    continue;
                case IElement { LocalName: "a" } a when NormalizeUploadPath(a.GetAttribute("href")) is not null:
                    any = true;
                    continue;
                default:
                    return false;
            }
        }
        return any;
    }

    /// <summary>有内联预览的上传链接路径；段落中指向它们的文字链接不再单独显示。</summary>
    public static HashSet<string> InlinePreviewPaths(IElement root) => InlineImageHrefs(root);

    /// <summary>解析消息 HTML，返回承载内容的 body 元素。</summary>
    public static IElement ParseFragment(string html) => Parse(html);

    /// <summary>是否包含非图片的上传文件。</summary>
    public static bool HasFileAttachment(string html) => ExtractUploads(html).Any(u => !u.IsImage);

    /// <summary>提取全部上传文件链接，按路径去重。</summary>
    public static IReadOnlyList<UploadLink> ExtractUploads(string html)
    {
        var root = Parse(html);
        var result = new List<UploadLink>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in root.QuerySelectorAll("a[href]"))
        {
            var path = NormalizeUploadPath(a.GetAttribute("href"));
            if (path is null || !seen.Add(path))
                continue;
            result.Add(UploadLinkOf(path, a.TextContent));
        }
        return result;
    }

    /// <summary>
    /// 由规范化的上传路径和链接文字生成 <see cref="UploadLink"/>。文件名优先用链接文字；
    /// 链接文字为空、是地址、或扩展名与上传路径不同（例如 <c>[报告](…/report.pdf)</c>）时，
    /// 用路径的最后一段，保证下载后的文件扩展名正确。
    /// </summary>
    public static UploadLink UploadLinkOf(string path, string? linkText)
    {
        var pathName = Uri.UnescapeDataString(path[(path.LastIndexOf('/') + 1)..]);
        var text = linkText?.Trim() ?? "";
        if (text.Length == 0 || text.Contains(UploadsPrefix, StringComparison.Ordinal) || text.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Path.GetExtension(text), Path.GetExtension(pathName), StringComparison.OrdinalIgnoreCase))
            text = pathName;
        return new UploadLink(path, text, IsImagePath(path));
    }

    /// <summary>
    /// 把链接规范为 <c>/user_uploads/…</c> 形式的相对路径（去掉查询串）；不是本组织的上传文件时返回 null。
    /// 服务器渲染时会把指向本组织的绝对链接改写为相对链接（形如 <c>user_uploads/…</c>，没有开头的斜杠；
    /// 已在 Zulip 12.3 上验证），所以带域名的链接都属于其它服务器，不当作上传文件。
    /// 缩略图路径（<c>/user_uploads/thumbnail/…</c>）不算上传文件。
    /// </summary>
    public static string? NormalizeUploadPath(string? href)
    {
        if (string.IsNullOrEmpty(href))
            return null;
        string path;
        if (href.StartsWith(UploadsPrefix, StringComparison.Ordinal))
            path = href;
        else if (href.StartsWith(UploadsPrefix[1..], StringComparison.Ordinal))
            path = "/" + href;
        else
            return null;
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        if (path.StartsWith(UploadsPrefix + "thumbnail/", StringComparison.Ordinal))
            return null;
        return path;
    }

    public static bool IsImagePath(string path) => ImageExtensions.Contains(Path.GetExtension(path));

    public static bool IsVideoPath(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// 把 <c>emoji-1f604</c>、<c>emoji-1f469-200d-1f4bb</c> 这样的 class 转为 Unicode 字符；无法解析时返回 null。
    /// </summary>
    public static string? EmojiFromClass(string? className)
    {
        if (className is null)
            return null;
        foreach (var cls in className.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!cls.StartsWith("emoji-", StringComparison.Ordinal))
                continue;
            var sb = new StringBuilder();
            foreach (var hex in cls["emoji-".Length..].Split('-'))
            {
                if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var cp)
                    || cp is < 0 or > 0x10FFFF or (>= 0xD800 and <= 0xDFFF))
                    return null;
                sb.Append(char.ConvertFromUtf32(cp));
            }
            return sb.Length > 0 ? sb.ToString() : null;
        }
        return null;
    }

    private static IElement Parse(string html)
    {
        var doc = Parser.ParseDocument("<!DOCTYPE html><html><body></body></html>");
        var body = doc.Body!;
        body.InnerHtml = html;
        return body;
    }

    /// <summary>有内联预览的图片链接：段落中指向同一图片的文字链接不再重复显示 [图片]。</summary>
    /// <summary>
    /// 消息中的内联图片预览（<c>div.message_inline_image</c>，不含视频），按出现顺序。
    /// <see cref="InlineImage.Src"/> 为显示用的缩略图（缩略图尚未生成时为上传路径），<see cref="InlineImage.Href"/> 为原链接。
    /// 与聊天中的渲染规则一致。
    /// </summary>
    public static IReadOnlyList<InlineImage> ExtractInlineImages(string html)
    {
        var result = new List<InlineImage>();
        foreach (var div in Parse(html).QuerySelectorAll("div.message_inline_image"))
        {
            if (div.ClassList.Contains("message_inline_video") || div.QuerySelector("img") is not { } img)
                continue;
            var href = div.QuerySelector("a[href]")?.GetAttribute("href") ?? "";
            var path = NormalizeUploadPath(href);
            var src = img.ClassList.Contains("image-loading-placeholder") && path is not null ? path : img.GetAttribute("src");
            if (!string.IsNullOrEmpty(src))
                result.Add(new InlineImage(src, href));
        }
        return result;
    }

    private static HashSet<string> InlineImageHrefs(IElement root)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in root.QuerySelectorAll("div.message_inline_image a[href]"))
        {
            if (NormalizeUploadPath(a.GetAttribute("href")) is { } path)
                set.Add(path);
        }
        return set;
    }

    /// <summary>
    /// 行内元素的替换文字；返回 null 表示没有特殊处理，需要递归子节点。
    /// <paramref name="inlineImages"/> 中的图片链接在段落里返回空字符串（由内联预览显示）。
    /// </summary>
    private static string? SpecialText(IElement e, HashSet<string> inlineImages)
    {
        var tag = e.LocalName;
        var cls = e.ClassList;

        if (cls.Contains("message_inline_image"))
            return "[图片]";
        if (cls.Contains("message_inline_video"))
            return "[视频]";
        if (cls.Contains("codehilite") || tag == "pre")
            return "[代码]";
        if (cls.Contains("katex-display") || cls.Contains("katex"))
            return "[公式]";
        if (cls.Contains("spoiler-block"))
            return "[剧透]";

        if (tag == "img")
            return cls.Contains("emoji") ? e.GetAttribute("alt") ?? "" : "[图片]";

        if (tag == "span" && cls.Contains("emoji"))
            return EmojiFromClass(e.GetAttribute("class")) ?? e.TextContent;

        if (tag == "span" && (cls.Contains("user-mention") || cls.Contains("user-group-mention") || cls.Contains("topic-mention")))
            return e.TextContent;

        if (tag == "time" && e.GetAttribute("datetime") is { } dt
            && DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
            return t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture);

        if (tag == "a" && NormalizeUploadPath(e.GetAttribute("href")) is { } path)
        {
            if (IsImagePath(path))
                return inlineImages.Contains(path) ? "" : "[图片]";
            if (IsVideoPath(path))
                return "[视频]";
            return "[文件]" + e.TextContent.Trim();
        }

        return null;
    }

    private static void AppendPreview(INode node, StringBuilder sb, HashSet<string> inlineImages)
    {
        if (node is IText text)
        {
            sb.Append(text.Data);
            return;
        }
        if (node is not IElement e)
            return;
        if (e.LocalName == "blockquote")
            return;

        var block = BlockTags.Contains(e.LocalName);
        if (block)
            sb.Append(' ');

        if (SpecialText(e, inlineImages) is { } special)
        {
            if (special.Length > 0)
                sb.Append(' ').Append(special).Append(' ');
        }
        else
        {
            foreach (var child in e.ChildNodes)
                AppendPreview(child, sb, inlineImages);
        }

        if (block)
            sb.Append(' ');
    }

    /// <summary>按块生成多行文本，每个块是若干行。</summary>
    private static void AppendPlainBlocks(INodeList nodes, List<string> lines, HashSet<string> inlineImages)
    {
        var inline = new StringBuilder();

        void FlushInline()
        {
            var s = CollapseSpaces().Replace(inline.ToString(), " ").Trim();
            if (s.Length > 0)
                lines.AddRange(s.Split('\n').Select(l => l.Trim()));
            inline.Clear();
        }

        foreach (var node in nodes)
        {
            if (node is IText t)
            {
                inline.Append(t.Data.Replace('\n', ' '));
                continue;
            }
            if (node is not IElement e)
                continue;

            switch (e.LocalName)
            {
                case "br":
                    inline.Append('\n');
                    continue;
                case "blockquote":
                    {
                        FlushInline();
                        var inner = new List<string>();
                        AppendPlainBlocks(e.ChildNodes, inner, inlineImages);
                        lines.AddRange(inner.Select(l => "> " + l));
                        continue;
                    }
                case "pre":
                    FlushInline();
                    lines.AddRange(e.TextContent.TrimEnd('\n').Split('\n'));
                    continue;
                case "ul":
                case "ol":
                    {
                        FlushInline();
                        var n = 1;
                        foreach (var li in e.Children.Where(c => c.LocalName == "li"))
                        {
                            var inner = new List<string>();
                            AppendPlainBlocks(li.ChildNodes, inner, inlineImages);
                            var prefix = e.LocalName == "ol" ? $"{n++}. " : "• ";
                            for (var i = 0; i < inner.Count; i++)
                                lines.Add((i == 0 ? prefix : "  ") + inner[i]);
                        }
                        continue;
                    }
            }

            if (e.ClassList.Contains("codehilite"))
            {
                FlushInline();
                var pre = e.QuerySelector("pre");
                lines.AddRange((pre?.TextContent ?? e.TextContent).TrimEnd('\n').Split('\n'));
                continue;
            }
            if (e.ClassList.Contains("message_inline_image"))
                continue; // 段落中的文字链接已经给出文件名

            if (e.ClassList.Contains("katex") || e.ClassList.Contains("katex-display"))
            {
                var tex = e.QuerySelector("annotation[encoding=\"application/x-tex\"]")?.TextContent;
                inline.Append(tex is null ? "[公式]" : "$$" + tex + "$$");
                continue;
            }

            if (BlockTags.Contains(e.LocalName))
            {
                FlushInline();
                AppendPlainBlocks(e.ChildNodes, lines, inlineImages);
                continue;
            }

            AppendPlainInline(e, inline, inlineImages);
        }

        FlushInline();
    }

    private static void AppendPlainInline(IElement e, StringBuilder sb, HashSet<string> inlineImages)
    {
        // 纯文本中文件和图片保留文件名，便于复制
        if (e.LocalName == "a" && NormalizeUploadPath(e.GetAttribute("href")) is not null)
        {
            sb.Append(e.TextContent);
            return;
        }
        if (e.LocalName == "a" && e.GetAttribute("href") is { } href && !href.StartsWith('#') && e.TextContent != href)
        {
            sb.Append(e.TextContent).Append(" (").Append(href).Append(')');
            return;
        }
        if (SpecialText(e, inlineImages) is { } special)
        {
            sb.Append(special);
            return;
        }
        foreach (var child in e.ChildNodes)
        {
            if (child is IText t)
                sb.Append(t.Data.Replace('\n', ' '));
            else if (child is IElement ce)
            {
                if (ce.LocalName == "br")
                    sb.Append('\n');
                else
                    AppendPlainInline(ce, sb, inlineImages);
            }
        }
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex CollapseWhitespace();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex CollapseSpaces();
}
