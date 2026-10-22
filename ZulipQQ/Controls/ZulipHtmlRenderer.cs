using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ZulipQQ.Core;
using HorizontalAlignment = Avalonia.Layout.HorizontalAlignment;

namespace ZulipQQ.Controls;

/// <summary>
/// 把 Zulip 渲染的消息 HTML 转为 Avalonia 控件。块级元素（段落、引用、代码、列表、表格、图片、剧透）各自成为一个控件，
/// 行内元素（加粗、斜体、链接、提及、表情、行内代码）成为段落中的 Run。
/// </summary>
internal static partial class ZulipHtmlRenderer
{
    private const double MaxImageSize = 240;

    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    // h1～h6 的字号资源（随设置中的字体大小变化）
    private static readonly string[] HeadingSizes = ["FontSizeLarge", "FontSizeH2", "FontSizeH3", "FontSizeBody", "FontSizeBody", "FontSizeBody"];

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "blockquote", "pre", "ul", "ol", "table", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
    };

    public static Control Build(string html, string? meSender)
    {
        var root = ZulipHtml.ParseFragment(html);
        var ctx = new Context(ZulipHtml.InlinePreviewPaths(root), meSender);
        var panel = NewStack();
        BuildBlocks(root.ChildNodes, panel, ctx);
        return panel;
    }

    private sealed class Context(HashSet<string> previews, string? meSender)
    {
        /// <summary>有内联预览的上传路径：只含这些链接的段落不再显示。</summary>
        public HashSet<string> Previews { get; } = previews;

        /// <summary>“/me” 消息：第一个段落前加上发送者名字，之后置为 null。</summary>
        public string? MePrefix { get; set; } = meSender;

        public bool Italic { get; } = meSender is not null;
    }

    private static StackPanel NewStack(double spacing = 6) => new() { Spacing = spacing };

    private static void BuildBlocks(IEnumerable<INode> nodes, Panel target, Context ctx)
    {
        var inline = new List<INode>();

        void Flush()
        {
            if (inline.Exists(n => n is not IText t || !string.IsNullOrWhiteSpace(t.Data)))
                target.Children.Add(Paragraph(inline, ctx));
            inline.Clear();
        }

        foreach (var node in nodes)
        {
            if (node is IElement e && BlockTags.Contains(e.LocalName))
            {
                Flush();
                BuildBlock(e, target, ctx);
            }
            else if (node is IText or IElement)
            {
                inline.Add(node);
            }
        }
        Flush();
    }

    private static void BuildBlock(IElement e, Panel target, Context ctx)
    {
        switch (e.LocalName)
        {
            case "p":
                if (ZulipHtml.IsUploadOnlyParagraph(e))
                    AddFileCards(e, target, ctx);
                else
                    target.Children.Add(Paragraph(e.ChildNodes, ctx));
                return;
            case "blockquote":
                target.Children.Add(Quote(e, ctx));
                return;
            case "pre":
                target.Children.Add(CodeBlock(e.TextContent));
                return;
            case "ul":
            case "ol":
                target.Children.Add(ListBlock(e, ctx));
                return;
            case "table":
                target.Children.Add(TableBlock(e, ctx));
                return;
            case "hr":
                var line = new Border { Height = 1, Margin = new Thickness(0, 4) };
                BindResource(line, Border.BackgroundProperty, line, "DividerBrush");
                target.Children.Add(line);
                return;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                target.Children.Add(Paragraph(e.ChildNodes, ctx, HeadingSizes[e.LocalName[1] - '1'], bold: true));
                return;
        }

        var cls = e.ClassList;
        if (cls.Contains("codehilite"))
            target.Children.Add(CodeBlock(e.QuerySelector("pre")?.TextContent ?? e.TextContent));
        else if (cls.Contains("message_inline_image"))
            AddInlinePreview(e, target, ctx);
        else if (cls.Contains("spoiler-block"))
            target.Children.Add(Spoiler(e, ctx));
        else
            BuildBlocks(e.ChildNodes, target, ctx);
    }

    // 只有上传链接的段落：每个文件一张卡片；已有内联预览的图片 / 视频不重复显示
    private static void AddFileCards(IElement p, Panel target, Context ctx)
    {
        foreach (var a in p.Children.Where(c => c.LocalName == "a"))
        {
            var path = ZulipHtml.NormalizeUploadPath(a.GetAttribute("href"))!;
            if (ctx.Previews.Contains(path))
                continue;
            target.Children.Add(new FileCard(UploadOf(path, a.TextContent), ZulipHtml.IsVideoPath(path))
            {
                HorizontalAlignment = HorizontalAlignment.Left,
            });
        }
    }

    private static void AddInlinePreview(IElement e, Panel target, Context ctx)
    {
        var href = e.QuerySelector("a[href]")?.GetAttribute("href") ?? "";
        var path = ZulipHtml.NormalizeUploadPath(href);
        if (e.ClassList.Contains("message_inline_video"))
        {
            if (path is not null)
                target.Children.Add(new FileCard(UploadOf(path, e.QuerySelector("a")?.GetAttribute("title") ?? ""), isVideo: true)
                {
                    HorizontalAlignment = HorizontalAlignment.Left,
                });
            return;
        }

        if (e.QuerySelector("img") is not { } img)
            return;
        // 缩略图尚未生成时 src 是加载动画，改用原图
        var src = img.ClassList.Contains("image-loading-placeholder") && path is not null ? path : img.GetAttribute("src");
        if (string.IsNullOrEmpty(src))
            return;

        var (width, height) = FitSize(img.GetAttribute("data-original-dimensions"));
        target.Children.Add(new RemoteImage
        {
            Width = width,
            Height = height,
            Source = src,
            Href = href.Length > 0 ? href : null,
            HorizontalAlignment = HorizontalAlignment.Left,
        });
    }

    /// <summary>按原图尺寸等比缩放到最大 240×240（不放大）；没有尺寸时用 16:9。</summary>
    private static (double Width, double Height) FitSize(string? dimensions)
    {
        var parts = dimensions?.Split('x');
        if (parts is not { Length: 2 }
            || !double.TryParse(parts[0], CultureInfo.InvariantCulture, out var w)
            || !double.TryParse(parts[1], CultureInfo.InvariantCulture, out var h)
            || w <= 0 || h <= 0)
            return (MaxImageSize, MaxImageSize * 9 / 16);
        var scale = Math.Min(1, Math.Min(MaxImageSize / w, MaxImageSize / h));
        return (Math.Max(24, Math.Round(w * scale)), Math.Max(24, Math.Round(h * scale)));
    }

    private static Control Quote(IElement e, Context ctx)
    {
        var inner = NewStack();
        BuildBlocks(e.ChildNodes, inner, ctx);
        BindResource(inner, TextElement.ForegroundProperty, inner, "TextSecondaryBrush");
        var border = new Border { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = inner };
        BindResource(border, Border.BorderBrushProperty, border, "PressedBrush");
        return border;
    }

    private static Control CodeBlock(string text)
    {
        var block = new SelectableTextBlock
        {
            Text = text.TrimEnd('\n'),
            FontFamily = Mono,
            TextWrapping = TextWrapping.NoWrap,
        };
        BindResource(block, TextBlock.FontSizeProperty, block, "FontSizeSmall");
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = block,
        };
        var border = new Border { CornerRadius = new CornerRadius(4), Padding = new Thickness(8, 6), Child = scroller };
        BindResource(border, Border.BackgroundProperty, border, "FillBrush");
        return border;
    }

    private static Control ListBlock(IElement list, Context ctx)
    {
        var panel = NewStack(2);
        var ordered = list.LocalName == "ol";
        var n = int.TryParse(list.GetAttribute("start"), CultureInfo.InvariantCulture, out var start) ? start : 1;
        foreach (var li in list.Children.Where(c => c.LocalName == "li"))
        {
            var bullet = new TextBlock { Text = ordered ? $"{n++}." : "•", Margin = new Thickness(0, 0, 6, 0) };
            var content = NewStack(2);
            BuildBlocks(li.ChildNodes, content, ctx);
            Grid.SetColumn(content, 1);
            panel.Children.Add(new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Children = { bullet, content } });
        }
        return panel;
    }

    private static Control TableBlock(IElement table, Context ctx)
    {
        var rows = table.QuerySelectorAll("tr").ToList();
        var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Children.Count(c => c.LocalName is "td" or "th"));
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < rows.Count; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var c = 0;
            foreach (var cell in rows[r].Children.Where(x => x.LocalName is "td" or "th"))
            {
                var border = new Border
                {
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    Padding = new Thickness(6, 3),
                    Child = Paragraph(cell.ChildNodes, ctx, bold: cell.LocalName == "th"),
                };
                BindResource(border, Border.BorderBrushProperty, border, "PressedBrush");
                Grid.SetRow(border, r);
                Grid.SetColumn(border, c++);
                grid.Children.Add(border);
            }
        }
        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = grid,
        };
    }

    // 剧透：标题行可点击，内容默认折叠
    private static Control Spoiler(IElement e, Context ctx)
    {
        var header = NewStack();
        if (e.QuerySelector(".spoiler-header") is { } h)
            BuildBlocks(h.ChildNodes, header, ctx);
        if (header.Children.Count == 0)
            header.Children.Add(new TextBlock { Text = "剧透" });

        var body = NewStack();
        if (e.QuerySelector(".spoiler-content") is { } content)
            BuildBlocks(content.ChildNodes, body, ctx);
        body.IsVisible = false;
        body.Margin = new Thickness(0, 6, 0, 0);

        var arrow = new TextBlock { Text = "▶", FontSize = 10, Margin = new Thickness(0, 3, 6, 0) };
        Grid.SetColumn(header, 1);
        var title = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Hand),
            Children = { arrow, header },
        };
        title.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left)
                return;
            args.Handled = true;
            body.IsVisible = !body.IsVisible;
            arrow.Text = body.IsVisible ? "▼" : "▶";
        };

        var border = new Border
        {
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 6),
            Child = new StackPanel { Children = { title, body } },
        };
        BindResource(border, Border.BackgroundProperty, border, "FillBrush");
        return border;
    }

    private static MessageTextBlock Paragraph(IEnumerable<INode> nodes, Context ctx, string? fontSizeKey = null, bool bold = false)
    {
        var block = new MessageTextBlock { TextWrapping = TextWrapping.Wrap };
        if (fontSizeKey is not null)
            BindResource(block, TextBlock.FontSizeProperty, block, fontSizeKey);
        if (bold)
            block.FontWeight = FontWeight.Bold;

        var builder = new InlineBuilder(block);
        var style = new InlineStyle { Italic = ctx.Italic };
        if (ctx.MePrefix is { } me)
        {
            builder.AddText(me + " ", style with { Bold = true }, null);
            ctx.MePrefix = null;
        }
        foreach (var node in nodes)
            builder.Append(node, style, null);
        return block;
    }

    private static UploadLink UploadOf(string path, string text) => ZulipHtml.UploadLinkOf(path, text);

    private static void BindResource(AvaloniaObject target, AvaloniaProperty property, IResourceHost host, string key) =>
        target.Bind(property, host.GetResourceObservable(key));

    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike, bool Code, bool Accent, bool Highlight, bool Deleted = false);

    /// <summary>把行内节点展开为扁平的 Run 列表，同时记录链接的字符区间。</summary>
    private sealed class InlineBuilder(MessageTextBlock block)
    {
        private int _length;
        private bool _atLineStart = true;
        private bool _endsWithSpace;

        public void Append(INode node, InlineStyle style, MessageLinkTarget? link)
        {
            if (node is IText text)
            {
                AddText(text.Data, style, link);
                return;
            }
            if (node is not IElement e)
                return;

            var cls = e.ClassList;
            switch (e.LocalName)
            {
                case "br":
                    AddRaw("\n");
                    return;
                case "strong" or "b":
                    Children(e, style with { Bold = true }, link);
                    return;
                case "em" or "i":
                    Children(e, style with { Italic = true }, link);
                    return;
                case "del" or "s" or "strike":
                    Children(e, style with { Strike = true }, link);
                    return;
                case "code":
                    AddText(e.TextContent, style with { Code = true }, link);
                    return;
                case "a":
                    var href = e.GetAttribute("href");
                    if (string.IsNullOrEmpty(href))
                        Children(e, style, link);
                    else
                        Children(e, style with { Accent = true }, LinkOf(href, e.TextContent));
                    return;
                case "time":
                    AddText(TimeText(e), style, link);
                    return;
                case "img":
                    if (cls.Contains("emoji") && e.GetAttribute("src") is { Length: > 0 } src)
                        AddUi(new RemoteImage { Width = 20, Height = 20, CornerRadius = 0, Source = src });
                    else
                        AddText(cls.Contains("emoji") ? e.GetAttribute("alt") ?? "" : "[图片]", style, link);
                    return;
            }

            if (cls.Contains("emoji"))
            {
                AddText(ZulipHtml.EmojiFromClass(e.GetAttribute("class")) ?? e.TextContent, style, link);
            }
            else if (cls.Contains("user-mention") || cls.Contains("user-group-mention") || cls.Contains("topic-mention"))
            {
                MessageLinkTarget? mention = long.TryParse(e.GetAttribute("data-user-id"), CultureInfo.InvariantCulture, out var userId)
                    ? new MentionTarget(userId)
                    : link;
                AddText(e.TextContent, style with { Accent = true }, mention);
            }
            else if (cls.Contains("katex") || cls.Contains("katex-display"))
            {
                var tex = e.QuerySelector("annotation[encoding=\"application/x-tex\"]")?.TextContent;
                AddText(tex ?? e.TextContent, style with { Code = true }, link);
            }
            // 编辑历史的差异：新增的文字高亮，删除的文字为红色删除线
            else if (cls.Contains("highlight_text_inserted"))
            {
                Children(e, style with { Highlight = true }, link);
            }
            else if (cls.Contains("highlight_text_deleted"))
            {
                Children(e, style with { Strike = true, Deleted = true }, link);
            }
            else if (cls.Contains("highlight"))
            {
                Children(e, style with { Highlight = true }, link);
            }
            else
            {
                Children(e, style, link);
            }
        }

        public void AddText(string text, InlineStyle style, MessageLinkTarget? link)
        {
            // 行内代码原样保留空白（字符串字面量、命令中的空格有意义）
            if (!style.Code)
            {
                text = Whitespace().Replace(text, " ");
                if (_atLineStart || _endsWithSpace)
                    text = text.TrimStart(' ');
            }
            if (text.Length == 0)
                return;

            var run = new Run(text);
            if (style.Bold)
                run.FontWeight = FontWeight.Bold;
            if (style.Italic)
                run.FontStyle = FontStyle.Italic;
            if (style.Strike)
                run.TextDecorations = TextDecorations.Strikethrough;
            if (style.Code)
            {
                run.FontFamily = Mono;
                BindResource(run, TextElement.BackgroundProperty, block, "FillBrush");
            }
            if (style.Highlight)
                BindResource(run, TextElement.BackgroundProperty, block, "SearchHighlightBrush");
            if (style.Accent)
                BindResource(run, TextElement.ForegroundProperty, block, "BrandBrush");
            if (style.Deleted)
                BindResource(run, TextElement.ForegroundProperty, block, "DangerBrush");

            block.Inlines!.Add(run);
            if (link is not null)
                block.AddLink(_length, _length + text.Length, link);
            _length += text.Length;
            _atLineStart = false;
            _endsWithSpace = text.EndsWith(' ');
        }

        private void AddRaw(string text)
        {
            block.Inlines!.Add(new Run(text));
            _length += text.Length;
            _atLineStart = text.EndsWith('\n');
            _endsWithSpace = false;
        }

        // 行内控件在文字布局中占一个字符
        private void AddUi(Control control)
        {
            block.Inlines!.Add(new InlineUIContainer(control) { BaselineAlignment = BaselineAlignment.TextBottom });
            _length += 1;
            _atLineStart = false;
            _endsWithSpace = false;
        }

        private void Children(IElement e, InlineStyle style, MessageLinkTarget? link)
        {
            foreach (var child in e.ChildNodes)
                Append(child, style, link);
        }

        private static MessageLinkTarget LinkOf(string href, string text) =>
            ZulipHtml.NormalizeUploadPath(href) is { } path ? new FileTarget(UploadOf(path, text)) : new UrlTarget(href);

        private static string TimeText(IElement e) =>
            e.GetAttribute("datetime") is { } dt
            && DateTimeOffset.TryParse(dt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
                ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
                : e.TextContent;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
