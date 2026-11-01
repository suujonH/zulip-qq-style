using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 渲染模式下把一段原文换成其它显示：隐藏符号（宽度为 0 的元素）、提及等显示文字、表情、图片缩略图、
/// 列表圆点、引用缩进、围栏的说明文字。颜色与字号由 <see cref="MarkdownColorizer"/> 设置。
/// </summary>
internal sealed class MarkdownElementGenerator(MarkdownEditor editor) : VisualLineElementGenerator
{
    private const double ImageSize = 80;
    private const double EmojiSize = 20;

    private readonly List<(int Offset, int Length, Func<VisualLineElement> Create)> _items = [];

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        _items.Clear();
        if (editor.IsSourceMode)
            return;
        // 生成的元素都不跨行，一个可视行只对应一个文档行（LastDocumentLine 此时还没有确定）
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
        var image = new RemoteImage { Width = ImageSize, Height = ImageSize, Source = url, Margin = new Avalonia.Thickness(2, 2) };
        return new InlineObjectElement(length, ImageHost(image, ImageSize + 2));
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
        MdFenceKind.Spoiler => string.IsNullOrEmpty(line.FenceInfo) ? "剧透" : $"剧透 {line.FenceInfo}",
        MdFenceKind.Math => "公式",
        _ => string.IsNullOrEmpty(line.FenceInfo) ? "代码" : $"代码 {line.FenceInfo}",
    };

    // 与消息中 <time> 的显示相同
    private static string TimeLabel(string? time) =>
        DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToLocalTime().ToString("yyyy/MM/dd HH:mm", CultureInfo.InvariantCulture)
            : time ?? "";
}
