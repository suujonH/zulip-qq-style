using System;
using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 按解析结果设置字体、字号、颜色：渲染模式下标题放大加粗、加粗 / 斜体 / 删除线、代码等宽、链接与提及为主题色，
/// 显示出来的符号为浅色；源码模式显示全部符号（浅色），字体字号与渲染模式相同，提及按原文显示。
/// </summary>
internal sealed class MarkdownColorizer(MarkdownEditor editor) : DocumentColorizingTransformer
{
    // h1～h6 的字号资源，与消息中的标题相同
    private static readonly string[] HeadingSizes = ["FontSizeLarge", "FontSizeH2", "FontSizeH3", "FontSizeBody", "FontSizeBody", "FontSizeBody"];

    private static readonly FontFamily Mono = ZulipHtmlRenderer.Mono;

    private DocumentLine? _line;

    protected override void ColorizeLine(DocumentLine docLine)
    {
        _line = docLine;
        var model = editor.Model;
        var index = docLine.LineNumber - 1;
        if (index >= model.Lines.Count)
            return;
        var line = model.Lines[index];
        if (line.Start != docLine.Offset || line.End != docLine.EndOffset)
            return;

        var tertiary = editor.Brush("TextTertiaryBrush");
        var brand = editor.Brush("BrandBrush");
        // 源码模式：所有符号都显示（与光标所在的行、元素相同，浅色），字体字号照常；提及、时间等按原文显示
        var source = editor.IsSourceMode;
        var lineRevealed = source || editor.IsLineRevealed(index);
        switch (line.Kind)
        {
            case MdLineKind.Heading:
                var size = editor.Size(HeadingSizes[Math.Clamp(line.HeadingLevel, 1, 6) - 1]);
                Change(line.ContentStart, line.End, e =>
                {
                    e.TextRunProperties.SetFontRenderingEmSize(size);
                    SetWeight(e, FontWeight.Bold);
                });
                break;
            case MdLineKind.FenceBody when line.Fence is MdFenceKind.Code or MdFenceKind.Math:
            case MdLineKind.IndentedCode:
                SetMono(line.Start, line.End);
                break;
            case MdLineKind.FenceOpen:
                var sub = editor.Size("FontSizeSub");
                Change(line.Start, line.End, e =>
                {
                    e.TextRunProperties.SetForegroundBrush(tertiary);
                    if (!lineRevealed)
                        e.TextRunProperties.SetFontRenderingEmSize(sub);
                });
                break;
            case MdLineKind.FenceClose or MdLineKind.Rule or MdLineKind.TableSeparator:
                Foreground(line.Start, line.End, tertiary);
                break;
        }
        if (line.QuoteDepth > 0 && line.Kind is not (MdLineKind.FenceOpen or MdLineKind.FenceClose))
            Foreground(line.ContentStart, line.End, editor.Brush("TextSecondaryBrush"));
        foreach (var prefix in line.Prefixes)
        {
            if (lineRevealed)
                Foreground(prefix.Start, prefix.End, tertiary);
            else if (prefix.Kind == MdPrefixKind.Bullet)
                Foreground(prefix.Start, prefix.End, editor.Brush("TextSecondaryBrush"));
        }

        foreach (var span in model.Spans)
        {
            if (span.Start < line.ContentStart || span.End > line.End)
                continue;
            var revealed = source || editor.IsRevealed(span);
            switch (span.Kind)
            {
                case MdSpanKind.Strong:
                    Change(span.ContentStart, span.ContentEnd, e => SetWeight(e, FontWeight.Bold));
                    break;
                case MdSpanKind.Em:
                    Change(span.ContentStart, span.ContentEnd, e => SetStyle(e, FontStyle.Italic));
                    break;
                case MdSpanKind.StrongEm:
                    Change(span.ContentStart, span.ContentEnd, e =>
                    {
                        SetWeight(e, FontWeight.Bold);
                        SetStyle(e, FontStyle.Italic);
                    });
                    break;
                case MdSpanKind.Del:
                    Change(span.ContentStart, span.ContentEnd, e => e.TextRunProperties.SetTextDecorations(TextDecorations.Strikethrough));
                    break;
                case MdSpanKind.Code:
                    var fill = editor.Brush("FillBrush");
                    SetMono(span.ContentStart, span.ContentEnd);
                    Change(span.ContentStart, span.ContentEnd, e => e.BackgroundBrush = fill);
                    break;
                case MdSpanKind.Math:
                    SetMono(span.ContentStart, span.ContentEnd);
                    break;
                case MdSpanKind.Link when span.ContentStart == span.ContentEnd && !revealed:
                    Foreground(span.Start, span.End, brand);
                    break;
                case MdSpanKind.Link:
                    Foreground(span.ContentStart, span.ContentEnd, brand);
                    break;
                case MdSpanKind.Url:
                    Foreground(span.Start, span.End, brand);
                    break;
                case MdSpanKind.UserMention or MdSpanKind.GroupMention or MdSpanKind.ChannelLink when source:
                    break;
                case MdSpanKind.UserMention or MdSpanKind.GroupMention or MdSpanKind.ChannelLink:
                    Foreground(span.Start, span.End, brand);
                    break;
                case MdSpanKind.UploadImage or MdSpanKind.UploadFile or MdSpanKind.UploadPlaceholder or MdSpanKind.Time or MdSpanKind.Emoji when source:
                    Foreground(span.Start, span.End, tertiary);
                    break;
                case MdSpanKind.Time:
                    var timeFill = editor.Brush("FillBrush");
                    Change(span.Start, span.End, e => e.TextRunProperties.SetBackgroundBrush(timeFill));
                    break;
                case MdSpanKind.UploadPlaceholder:
                    var placeholderFill = editor.Brush("FillBrush");
                    var secondary = editor.Brush("TextSecondaryBrush");
                    Change(span.Start, span.End, e =>
                    {
                        e.TextRunProperties.SetBackgroundBrush(placeholderFill);
                        e.TextRunProperties.SetForegroundBrush(secondary);
                    });
                    break;
            }
            if (!ZulipMarkdown.IsAtomic(span.Kind) && revealed)
                Symbols(span, tertiary);
        }
    }

    private void Symbols(MdSpan span, IBrush brush)
    {
        Foreground(span.Start, span.ContentStart, brush);
        Foreground(span.ContentEnd, span.End, brush);
    }

    private void Foreground(int start, int end, IBrush brush) =>
        Change(start, end, e => e.TextRunProperties.SetForegroundBrush(brush));

    private void SetMono(int start, int end)
    {
        var small = editor.Size("FontSizeSmall");
        Change(start, end, e =>
        {
            var t = e.TextRunProperties.Typeface;
            e.TextRunProperties.SetTypeface(new Typeface(Mono, t.Style, t.Weight));
            e.TextRunProperties.SetFontRenderingEmSize(small);
        });
    }

    private static void SetWeight(VisualLineElement e, FontWeight weight)
    {
        var t = e.TextRunProperties.Typeface;
        e.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, t.Style, weight));
    }

    private static void SetStyle(VisualLineElement e, FontStyle style)
    {
        var t = e.TextRunProperties.Typeface;
        e.TextRunProperties.SetTypeface(new Typeface(t.FontFamily, style, t.Weight));
    }

    // 只改本行范围内的部分（ChangeLinePart 不接受行外的位置）
    private void Change(int start, int end, Action<VisualLineElement> action)
    {
        if (_line is null)
            return;
        start = Math.Max(start, _line.Offset);
        end = Math.Min(end, _line.EndOffset);
        if (start < end)
            ChangeLinePart(start, end, action);
    }
}
