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
using VerticalAlignment = Avalonia.Layout.VerticalAlignment;

namespace ZulipQQ.Controls;

/// <summary>
/// 把 Zulip 渲染的消息 HTML 转为 Avalonia 控件。块级元素（段落、引用、代码、列表、表格、图片、剧透）各自成为一个控件，
/// 行内元素（加粗、斜体、链接、提及、表情、行内代码）成为段落中的 Run。
/// </summary>
internal static partial class ZulipHtmlRenderer
{
    private const double MaxImageSize = 240;

    internal static readonly FontFamily Mono = new("Cascadia Mono, Consolas, Microsoft YaHei UI");
    // h1～h6 的字号资源（随设置中的字体大小变化）
    private static readonly string[] HeadingSizes = ["FontSizeLarge", "FontSizeH2", "FontSizeH3", "FontSizeBody", "FontSizeBody", "FontSizeBody"];

    private static readonly HashSet<string> BlockTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "div", "blockquote", "pre", "ul", "ol", "table", "hr", "h1", "h2", "h3", "h4", "h5", "h6",
    };

    /// <param name="isOwn">自己发送的消息（浅蓝色气泡）：链接和提及使用更深的颜色，保证可读。</param>
    public static Control Build(string html, string? meSender, bool isOwn = false)
    {
        var root = ZulipHtml.ParseFragment(html);
        var ctx = new Context(ZulipHtml.InlinePreviewPaths(root), ZulipHtml.InlinedPreviews(root), meSender)
        {
            AccentKey = isOwn ? "OwnLinkBrush" : "BrandBrush",
        };
        var panel = NewStack();
        BuildBlocks(root.ChildNodes, panel, ctx);
        return panel;
    }

    private sealed class Context(HashSet<string> previews, Dictionary<IElement, IElement> inlined, string? meSender)
    {
        /// <summary>有内联预览的上传路径：段落中指向它们的文件名链接不显示，只含这些链接的段落整段不显示。</summary>
        public HashSet<string> Previews { get; } = previews;

        /// <summary>与文字同一段落的上传图片链接与它的预览：图片显示在链接处，段落之后的预览不再显示。</summary>
        public Dictionary<IElement, IElement> Inlined { get; } = inlined;

        public HashSet<IElement> InlinedPreviews { get; } = [.. inlined.Values];

        /// <summary>“/me” 消息：第一个段落前加上发送者名字，之后置为 null。</summary>
        public string? MePrefix { get; set; } = meSender;

        public bool Italic { get; } = meSender is not null;

        /// <summary>链接、提及的颜色资源：自己的气泡中为 OwnLinkBrush。</summary>
        public string AccentKey { get; init; } = "BrandBrush";
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
                if (IsQuoteHeader(e))
                    return;
                if (ZulipHtml.IsUploadOnlyParagraph(e))
                    AddFileCards(e, target, ctx);
                else
                    target.Children.Add(Paragraph(e.ChildNodes, ctx));
                return;
            case "blockquote":
                target.Children.Add(Quote(e, ctx));
                return;
            case "pre":
                target.Children.Add(CodeBlock(e.TextContent, null));
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
                var heading = Paragraph(e.ChildNodes, ctx, HeadingSizes[e.LocalName[1] - '1'], bold: true);
                heading.MarkdownPrefix = new string('#', e.LocalName[1] - '0') + " ";
                target.Children.Add(heading);
                return;
        }

        var cls = e.ClassList;
        if (cls.Contains("codehilite"))
            target.Children.Add(CodeBlock(e.QuerySelector("pre")?.TextContent ?? e.TextContent, e.GetAttribute("data-code-language")));
        else if (cls.Contains("message_inline_image"))
            AddInlinePreview(e, target, ctx);
        else if (cls.Contains("spoiler-block"))
            target.Children.Add(Spoiler(e, ctx));
        else
            BuildBlocks(e.ChildNodes, target, ctx);
    }

    // 只有上传链接的段落：每个文件一张卡片；已有内联预览的图片 / 视频不重复显示。
    // 显示类型以服务器为准：没有预览的都是文件（即使扩展名是图片 / 视频）
    private static void AddFileCards(IElement p, Panel target, Context ctx)
    {
        foreach (var a in p.Children.Where(c => c.LocalName == "a"))
        {
            var path = ZulipHtml.NormalizeUploadPath(a.GetAttribute("href"))!;
            if (ctx.Previews.Contains(path))
                continue;
            target.Children.Add(new FileCard(UploadOf(path, a.TextContent), isVideo: false)
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

        // 已显示在段落中的链接处
        if (ctx.InlinedPreviews.Contains(e))
            return;
        if (PreviewImage(e) is { } image)
        {
            image.HorizontalAlignment = HorizontalAlignment.Left;
            target.Children.Add(image);
        }
    }

    // 上传图片的预览图（div.message_inline_image）；没有图片时为 null
    private static RemoteImage? PreviewImage(IElement e)
    {
        var href = e.QuerySelector("a[href]")?.GetAttribute("href") ?? "";
        var path = ZulipHtml.NormalizeUploadPath(href);
        if (e.QuerySelector("img") is not { } img)
            return null;
        // 缩略图尚未生成时 src 是加载动画，改用原图
        var src = img.ClassList.Contains("image-loading-placeholder") && path is not null ? path : img.GetAttribute("src");
        if (string.IsNullOrEmpty(src))
            return null;

        var (width, height) = FitSize(img.GetAttribute("data-original-dimensions"));
        return new RemoteImage
        {
            Width = width,
            Height = height,
            Source = src,
            Href = href.Length > 0 ? href : null,
        };
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

    /// <summary>
    /// 引用卡片（仿 QQ）：半透明圆角底色，左侧竖线。引用回复（有原消息链接）第一行显示原作者，
    /// 右上角显示跳转图标，悬停时底色加深，点击整张卡片跳转到原消息（与点击“说”相同）；
    /// 没有原消息链接的普通引用没有图标，也不响应点击。
    /// </summary>
    private static Control Quote(IElement e, Context ctx)
    {
        var source = QuoteSourceOf(e.PreviousElementSibling);
        var inner = NewStack(4);
        if (source?.Author is { Length: > 0 } author)
        {
            var header = new TextBlock { Text = author, TextTrimming = TextTrimming.CharacterEllipsis };
            BindResource(header, TextBlock.FontSizeProperty, header, "FontSizeSub");
            inner.Children.Add(header);
        }
        BuildBlocks(e.ChildNodes, inner, ctx);
        BindResource(inner, TextElement.ForegroundProperty, inner, "TextSecondaryBrush");

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { inner } };
        var bar = new Border { BorderThickness = new Thickness(2, 0, 0, 0), Padding = new Thickness(8, 0, 0, 0), Child = grid };
        BindResource(bar, Border.BorderBrushProperty, bar, "QuoteBarBrush");
        var card = new Border { CornerRadius = new CornerRadius(6), Padding = new Thickness(8, 6), Child = bar };
        var background = card.Bind(Border.BackgroundProperty, card.GetResourceObservable("QuoteCardBrush"));
        if (source is null)
            return card;

        var icon = new TdIcon
        {
            Icon = Application.Current?.FindResource("Icon.Backtop") as TdIconData,
            Width = 14,
            Height = 14,
            Margin = new Thickness(8, 2, 0, 0),
            VerticalAlignment = VerticalAlignment.Top,
        };
        BindResource(icon, TdIcon.ForegroundProperty, icon, "TextTertiaryBrush");
        ToolTip.SetTip(icon, "跳转到原消息");
        Grid.SetColumn(icon, 1);
        grid.Children.Add(icon);

        card.Cursor = new Cursor(StandardCursorType.Hand);
        void SetBackground(string key)
        {
            background.Dispose();
            background = card.Bind(Border.BackgroundProperty, card.GetResourceObservable(key));
        }
        card.PointerEntered += (_, _) => SetBackground("QuoteCardHoverBrush");
        card.PointerExited += (_, _) => SetBackground("QuoteCardBrush");
        // 引用中的链接、提及先处理点击；拖动选择了文字时不跳转
        card.PointerReleased += (_, args) =>
        {
            if (args.InitialPressMouseButton != MouseButton.Left
                || args.Source is SelectableTextBlock { } text && text.SelectionStart != text.SelectionEnd)
                return;
            args.Handled = true;
            card.RaiseEvent(new MessageLinkEventArgs(MessageContent.LinkClickedEvent, new UrlTarget(source.Href)));
        };
        return card;
    }

    /// <summary>引用回复的原作者（可能为 null）与原消息链接。</summary>
    private sealed record QuoteSource(string? Author, string Href);

    /// <summary>
    /// 识别 Zulip 引用回复的标题段落 <c>@_**某人|id** [说](…/near/id):</c>，即只有一个提及、一个指向消息的链接和冒号。
    /// 不是这种段落时返回 null。这个段落不再单独显示，内容并入其后的引用卡片。
    /// </summary>
    private static QuoteSource? QuoteSourceOf(IElement? p)
    {
        if (p is not { LocalName: "p" })
            return null;
        string? author = null;
        string? href = null;
        var colon = false;
        foreach (var node in p.ChildNodes)
        {
            switch (node)
            {
                case IText t when t.Data.Trim() is "":
                    continue;
                case IText t when t.Data.Trim() is ":" or "：":
                    colon = true;
                    continue;
                case IElement s when author is null && href is null && s.ClassList.Contains("user-mention"):
                    author = s.TextContent.Trim().TrimStart('@');
                    continue;
                case IElement { LocalName: "a" } a when href is null
                    && a.GetAttribute("href") is { } h && h.StartsWith("#narrow/", StringComparison.Ordinal) && h.Contains("/near/", StringComparison.Ordinal):
                    href = h;
                    continue;
                default:
                    return null;
            }
        }
        // 引用回复的头部总是“@作者 [说](链接):”；缺少作者或冒号时只是普通段落，不能当作引用头部隐藏
        return href is null || author is null || !colon ? null : new QuoteSource(author, href);
    }

    /// <summary>引用回复的标题段落（后面紧跟引用块）：由引用卡片显示，段落本身跳过。</summary>
    private static bool IsQuoteHeader(IElement e) =>
        e.NextElementSibling is { LocalName: "blockquote" } && QuoteSourceOf(e) is not null;

    // language：围栏标注的语言，复制时保留
    private static Control CodeBlock(string text, string? language)
    {
        var block = new MessageTextBlock
        {
            Text = text.TrimEnd('\n'),
            FontFamily = Mono,
            TextWrapping = TextWrapping.NoWrap,
            IsCodeBlock = true,
            CodeLanguage = language,
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
            if (content.Children.FirstOrDefault() is MessageTextBlock first)
                first.ListMarker = bullet.Text;
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

        var builder = new InlineBuilder(block, ctx);
        var style = new InlineStyle { Italic = ctx.Italic };
        if (ctx.MePrefix is { } me)
        {
            builder.AddText(me + " ", style with { Bold = true }, null, "/me ");
            ctx.MePrefix = null;
        }
        foreach (var node in nodes)
            builder.Append(node, style, null);
        return block;
    }

    private static UploadLink UploadOf(string path, string text) => ZulipHtml.UploadLinkOf(path, text);

    private static void BindResource(AvaloniaObject target, AvaloniaProperty property, IResourceHost host, string key) =>
        target.Bind(property, host.GetResourceObservable(key));

    // Href：所在链接的地址，复制时转回 [文字](地址)
    private readonly record struct InlineStyle(bool Bold, bool Italic, bool Strike, bool Code, bool Accent, bool Highlight, bool Deleted = false,
        string? Href = null);

    /// <summary>把行内节点展开为扁平的 Run 列表，同时记录链接的字符区间。</summary>
    /// <param name="ctx">有内联预览的上传路径：段落中指向它们的文件名链接不显示（与文字同一段落的图片显示在链接处）。</param>
    private sealed class InlineBuilder(MessageTextBlock block, Context ctx)
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
                    else if (ctx.Inlined.TryGetValue(e, out var preview))
                    {
                        if (PreviewImage(preview) is { } image)
                            AddUi(image, $"[{e.TextContent}]({href})");
                    }
                    else if (ZulipHtml.NormalizeUploadPath(href) is { } path && ctx.Previews.Contains(path)
                             && ZulipHtml.IsFileNameLabel(e.TextContent, path))
                        return;
                    else if (ZulipHtml.NormalizeUploadPath(href) is { } filePath && !ctx.Previews.Contains(filePath))
                    {
                        // 服务器没有生成预览的上传文件（服务器不当作图片 / 视频）：不论前后有没有文字，都在链接处显示为文件卡片
                        AddUi(new FileCard(UploadOf(filePath, e.TextContent), isVideo: false), $"[{e.TextContent}]({href})");
                    }
                    else if (ChannelMarkdown(e) is { } channel)
                        AddText(e.TextContent, style with { Accent = true }, LinkOf(href, e.TextContent), channel);
                    else
                        Children(e, style with { Accent = true, Href = href }, LinkOf(href, e.TextContent));
                    return;
                case "time":
                    AddText(TimeText(e), style, link, e.GetAttribute("datetime") is { } datetime ? $"<time:{datetime}>" : null);
                    return;
                case "img":
                    if (cls.Contains("emoji") && e.GetAttribute("src") is { Length: > 0 } src)
                    {
                        AddUi(new RemoteImage { Width = 20, Height = 20, CornerRadius = 0, Source = src }, e.GetAttribute("alt"));
                    }
                    else if (cls.Contains("inline-image") && ZulipHtml.InlineImageSource(e) is ({ Length: > 0 } imageSrc, var imageHref))
                    {
                        // 正文图片（![](…)）：与内联预览一样按原图比例缩放，点击打开查看器
                        var (width, height) = FitSize(e.GetAttribute("data-original-dimensions"));
                        AddUi(new RemoteImage { Width = width, Height = height, Source = imageSrc, Href = imageHref },
                            $"![]({imageHref ?? imageSrc})");
                    }
                    else
                        AddText(cls.Contains("emoji") ? e.GetAttribute("alt") ?? "" : "[图片]", style, link);
                    return;
            }

            if (cls.Contains("emoji"))
            {
                // 文字为 :名称:
                AddText(ZulipHtml.EmojiFromClass(e.GetAttribute("class")) ?? e.TextContent, style, link, e.TextContent);
            }
            else if (cls.Contains("user-mention") || cls.Contains("user-group-mention") || cls.Contains("topic-mention"))
            {
                // 静默提及的文字没有 @
                var name = e.TextContent.TrimStart('@');
                MessageLinkTarget? mention =
                    long.TryParse(e.GetAttribute("data-user-id"), CultureInfo.InvariantCulture, out var userId) ? new MentionTarget(userId)
                    : cls.Contains("user-group-mention") ? new GroupMentionTarget(name)
                    : cls.Contains("topic-mention") ? new WildcardMentionTarget("topic")
                    : e.GetAttribute("data-user-id") == "*" ? new WildcardMentionTarget(name is "everyone" or "channel" or "stream" ? name : "all")
                    : link;
                AddText(ZulipHtml.MentionDisplay(e), style with { Accent = true }, mention, MentionMarkdown(e));
            }
            else if (cls.Contains("katex") || cls.Contains("katex-display"))
            {
                var tex = e.QuerySelector("annotation[encoding=\"application/x-tex\"]")?.TextContent;
                AddText(tex ?? e.TextContent, style with { Code = true }, link, tex is null ? null : $"$${tex}$$");
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

        /// <param name="markdown">整体元素（提及、表情等）的 Markdown；null 为普通文字。</param>
        public void AddText(string text, InlineStyle style, MessageLinkTarget? link, string? markdown = null)
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
                // 背景由 MessageTextBlock 画成带内边距的圆角块（Run 的背景只是紧贴文字的方块）
                run.FontFamily = Mono;
                block.AddCodeRange(_length, _length + text.Length);
            }
            if (style.Highlight)
                BindResource(run, TextElement.BackgroundProperty, block, "SearchHighlightBrush");
            if (style.Accent)
                BindResource(run, TextElement.ForegroundProperty, block, ctx.AccentKey);
            if (style.Deleted)
                BindResource(run, TextElement.ForegroundProperty, block, "DangerBrush");

            block.Inlines!.Add(run);
            if (link is not null)
                block.AddLink(_length, _length + text.Length, link);
            block.AddSource(text, new InlinePiece(_length, _length + text.Length, markdown,
                style.Bold, style.Italic, style.Strike, style.Code, style.Href));
            _length += text.Length;
            _atLineStart = false;
            _endsWithSpace = text.EndsWith(' ');
        }

        private void AddRaw(string text)
        {
            block.Inlines!.Add(new Run(text));
            block.AddSource(text, null);
            _length += text.Length;
            _atLineStart = text.EndsWith('\n');
            _endsWithSpace = false;
        }

        // 行内控件在文字布局中占一个字符；markdown 为它的 Markdown。图片双击打开
        private void AddUi(Control control, string? markdown)
        {
            if (control is RemoteImage image)
                image.OpenOnDoubleClick = true;
            block.Inlines!.Add(new InlineUIContainer(control) { BaselineAlignment = BaselineAlignment.TextBottom });
            block.AddSource("￼", new InlinePiece(_length, _length + 1, markdown ?? ""));
            _length += 1;
            _atLineStart = false;
            _endsWithSpace = false;
        }

        private void Children(IElement e, InlineStyle style, MessageLinkTarget? link)
        {
            foreach (var child in e.ChildNodes)
                Append(child, style, link);
        }

        // 提及的 Markdown：静默提及为 @_，用户组为 @*名字*，用户带 id
        private static string MentionMarkdown(IElement e)
        {
            var at = e.ClassList.Contains("silent") ? "@_" : "@";
            var name = e.TextContent.TrimStart('@');
            if (e.ClassList.Contains("topic-mention"))
                return $"{at}**topic**";
            if (e.ClassList.Contains("user-group-mention"))
                return $"{at}*{name}*";
            return e.GetAttribute("data-user-id") is { Length: > 0 } id && id != "*" ? $"{at}**{name}|{id}**" : $"{at}**{name}**";
        }

        // 频道、话题链接（#频道、#频道 > 话题）的 Markdown；其它链接为 null
        private static string? ChannelMarkdown(IElement a)
        {
            var text = a.TextContent;
            if (!text.StartsWith('#'))
                return null;
            if (a.ClassList.Contains("stream"))
                return $"#**{text[1..]}**";
            if (a.ClassList.Contains("stream-topic") && text.IndexOf(" > ", StringComparison.Ordinal) is var gt and > 0)
                return $"#**{text[1..gt]}>{text[(gt + 3)..]}**";
            return null;
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
