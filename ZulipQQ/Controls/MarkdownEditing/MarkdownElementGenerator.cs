using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 渲染模式下把一段原文换成其它显示：隐藏符号（宽度为 0 的元素）、提及等显示文字、表情、图片缩略图、视频与文件的卡片、
/// 列表圆点、引用缩进、围栏的说明文字、引用消息的面板、表格的 |。颜色与字号由 <see cref="MarkdownColorizer"/> 设置。
/// </summary>
internal sealed class MarkdownElementGenerator(MarkdownEditor editor) : VisualLineElementGenerator
{
    private const double ImageSize = 80;
    private const double EmojiSize = 20;
    private const double FileChipHeight = 26;
    private const double FileChipMaxWidth = 220;

    private readonly List<(int Offset, int Length, Func<VisualLineElement> Create)> _items = [];

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _items.Clear();
        if (editor.IsSourceMode)
            return;
        // 只按可视行的第一个文档行生成（LastDocumentLine 此时还没有确定）：除引用消息的面板外，元素都不跨行
        var model = editor.Model;
        var index = context.VisualLine.FirstDocumentLine.LineNumber - 1;
        if (index < model.Lines.Count)
            AddLine(model, index);
        _items.Sort((a, b) => a.Offset.CompareTo(b.Offset));
        // 去掉与前一项重叠的项
        for (var i = _items.Count - 1; i > 0; i--)
        {
            var prev = _items[i - 1];
            if (_items[i].Offset < prev.Offset + prev.Length)
                _items.RemoveAt(i);
        }
    }

    public override int GetFirstInterestedOffset(int startOffset)
    {
        foreach (var item in _items)
            if (item.Offset >= startOffset)
                return item.Offset;
        return -1;
    }

    public override VisualLineElement? ConstructElement(int offset)
    {
        foreach (var item in _items)
            if (item.Offset == offset)
                return item.Create();
        return null;
    }

    private void AddLine(MarkdownModel model, int index)
    {
        var line = model.Lines[index];
        // 文档中有 \r 时行的划分可能与解析结果不一致（OnTextChanged 后会重新解析），不生成
        if (CurrentContext.Document.GetLineByNumber(index + 1) is not { } docLine || docLine.Offset != line.Start || docLine.EndOffset != line.End)
            return;
        var lineRevealed = editor.IsLineRevealed(index);
        var length = line.End - line.Start;

        // 引用消息：从引用行到围栏结束的整段显示为一个面板（元素跨行，可视行包含这几行）
        // 结束行与文档不一致（文档已修改、还没有重新解析）时不生成：元素不能在换行符中间结束
        if (editor.Quotes.FirstOrDefault(q => q.HeaderLine == index) is { } quote && !editor.IsQuoteRevealed(quote)
            && quote.CloseLine < CurrentContext.Document.LineCount
            && CurrentContext.Document.GetLineByNumber(quote.CloseLine + 1).EndOffset == quote.End)
        {
            var quoteLength = quote.End - quote.Start;
            var width = Math.Max(120, CurrentContext.TextView.Bounds.Width - 4);
            Add(quote.Start, quoteLength, () => new InlineObjectElement(quoteLength, new QuotePanel(editor, quote, width)));
            return;
        }

        // 单元格的自动换行
        foreach (var soft in editor.SoftBreaksOn(index))
        {
            var (start, softLength, kind) = (line.Start + soft.Start, soft.Length, soft.Kind);
            var character = CurrentContext.Document.GetText(start, 1);
            Add(start, softLength, () => new SoftBreakElement(kind, character, softLength));
        }

        // 表格：整段显示为一个控件（元素跨行，其它行已折叠）
        if (editor.Tables.FirstOrDefault(t => t.HeaderLine == index) is { } table && !editor.IsSourceMode
            && table.LastLine < CurrentContext.Document.LineCount
            && CurrentContext.Document.GetLineByNumber(table.LastLine + 1).EndOffset == model.Lines[table.LastLine].End
            && editor.PanelOf(table) is { } panel)
        {
            var tableLength = model.Lines[table.LastLine].End - line.Start;
            Add(line.Start, tableLength, () => new InlineObjectElement(tableLength, panel));
            return;
        }

        if (!lineRevealed && length > 0)
        {
            switch (line.Kind)
            {
                case MdLineKind.FenceOpen:
                    Add(line.Start, length, () => Text(FenceLabel(line), length));
                    return;
                case MdLineKind.FenceClose or MdLineKind.Rule:
                    Add(line.Start, length, () => Hidden(length));
                    return;
            }
        }

        if (!lineRevealed)
        {
            foreach (var prefix in line.Prefixes)
            {
                var len = prefix.End - prefix.Start;
                switch (prefix.Kind)
                {
                    // 引用的竖线由背景层画，这里留出缩进
                    case MdPrefixKind.Quote:
                        Add(prefix.Start, len, () => Text("  ", len));
                        break;
                    case MdPrefixKind.Heading:
                        Add(prefix.Start, len, () => Hidden(len));
                        break;
                    case MdPrefixKind.Bullet:
                        Add(prefix.Start, len, () => Text("• ", len));
                        break;
                }
            }
        }

        foreach (var span in model.Spans)
        {
            if (span.Start < line.ContentStart || span.End > line.End)
                continue;
            var len = span.End - span.Start;
            switch (span.Kind)
            {
                case MdSpanKind.UserMention or MdSpanKind.GroupMention or MdSpanKind.ChannelLink:
                    Add(span.Start, len, () => Text(span.Data ?? "", len));
                    break;
                case MdSpanKind.Emoji when EmojiText(span.Data) is { } emoji:
                    Add(span.Start, len, () => Text(emoji, len));
                    break;
                case MdSpanKind.Emoji:
                    Add(span.Start, len, () => EmojiImage(span.Data, len));
                    break;
                case MdSpanKind.Time:
                    Add(span.Start, len, () => Text(TimeLabel(span.Data), len));
                    break;
                case MdSpanKind.UploadImage:
                    Add(span.Start, len, () => Image(span.Data, len));
                    break;
                case MdSpanKind.UploadFile:
                    var fileName = CurrentContext.Document.GetText(span.ContentStart, span.ContentEnd - span.ContentStart);
                    Add(span.Start, len, () => FileChip(span.Data, fileName, len));
                    break;
                case MdSpanKind.UploadPlaceholder:
                    Add(span.Start, len, () => Text($"上传中：{span.Data}…", len));
                    break;
                case MdSpanKind.Link when span.ContentStart == span.ContentEnd && !editor.IsRevealed(span):
                    Add(span.Start, len, () => Text(span.Data ?? "", len));
                    break;
                case MdSpanKind.Url:
                    break;
                default:
                    if (editor.IsRevealed(span))
                        break;
                    var head = span.ContentStart - span.Start;
                    var tail = span.End - span.ContentEnd;
                    if (head > 0)
                        Add(span.Start, head, () => Hidden(head));
                    if (tail > 0)
                        Add(span.ContentEnd, tail, () => Hidden(tail));
                    break;
            }
        }
    }

    private void Add(int offset, int length, Func<VisualLineElement> create)
    {
        if (length > 0)
            _items.Add((offset, length, create));
    }

    private static VisualLineElement Hidden(int length) =>
        new InlineObjectElement(length, new Border { Width = 0, Height = 0 });

    // 与正文同样排版的文字（基线对齐，颜色字号由着色器按区间设置）
    private static VisualLineElement Text(string text, int length) =>
        new FormattedTextElement(text.Length == 0 ? " " : text, length);

    private static Control ImageHost(Control image, double baseline)
    {
        TextBlock.SetBaselineOffset(image, baseline);
        return image;
    }

    private static VisualLineElement Image(string? url, int length)
    {
        var image = new RemoteImage { Width = ImageSize, Height = ImageSize, Source = url, Margin = new Thickness(2, 2) };
        return new InlineObjectElement(length, ImageHost(image, ImageSize + 2));
    }

    // 视频与文件：类型图标加文件名的卡片。不接收指针，点击时照常放置光标
    private static VisualLineElement FileChip(string? url, string name, int length)
    {
        var icon = new TdIcon { Width = 16, Height = 16, VerticalAlignment = VerticalAlignment.Center };
        icon.Bind(TdIcon.IconProperty, icon.GetResourceObservable(FileKinds.IconKey(url ?? "")));
        icon.Bind(TdIcon.ForegroundProperty, icon.GetResourceObservable("BrandBrush"));
        var label = new TextBlock
        {
            Text = name,
            Margin = new Thickness(6, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        label.Bind(TextBlock.FontSizeProperty, label.GetResourceObservable("FontSizeSmall"));
        DockPanel.SetDock(icon, Dock.Left);
        var chip = new Border
        {
            Height = FileChipHeight,
            MaxWidth = FileChipMaxWidth,
            Margin = new Thickness(2),
            Padding = new Thickness(8, 0),
            CornerRadius = new CornerRadius(6),
            IsHitTestVisible = false,
            Child = new DockPanel { Children = { icon, label } },
        };
        chip.Bind(Border.BackgroundProperty, chip.GetResourceObservable("FillBrush"));
        return new InlineObjectElement(length, ImageHost(chip, FileChipHeight - 6));
    }

    private VisualLineElement EmojiImage(string? name, int length)
    {
        var url = name is null ? null : editor.EmojiLookup?.Invoke(name)?.ImageUrl;
        var image = new RemoteImage { Width = EmojiSize, Height = EmojiSize, CornerRadius = 0, Source = url };
        // 底部比文字基线低一些，与文字居中
        return new InlineObjectElement(length, ImageHost(image, EmojiSize - 4));
    }

    private string? EmojiText(string? name) =>
        name is null ? null : editor.EmojiLookup?.Invoke(name) is { ImageUrl: null, Text: { } text } ? text : null;

    private static string FenceLabel(MdLine line) => line.Fence switch
    {
        MdFenceKind.Quote => "引用",
        MdFenceKind.Spoiler => string.IsNullOrEmpty(line.FenceInfo) ? "折叠" : $"折叠 {line.FenceInfo}",
        MdFenceKind.Math => "公式",
        _ => string.IsNullOrEmpty(line.FenceInfo) ? "代码" : $"代码 {line.FenceInfo}",
    };

    // 与消息中 <time> 的显示相同
    private static string TimeLabel(string? time) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : time ?? "";
}
