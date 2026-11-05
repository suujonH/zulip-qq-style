using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Media;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 渲染模式下的块背景：引用为引用卡片底色，代码 / 公式 / 剧透为浅灰底色；连续的同类行合成一块。
/// 隐藏了 "> " 的行在左侧画竖线（"> " 换成了缩进），隐藏的分隔线（---）画一条横线。
/// </summary>
internal sealed class MarkdownBackgroundRenderer(MarkdownEditor editor) : IBackgroundRenderer
{
    private const double Radius = 6;
    private const double BarWidth = 3;

    private enum BlockKind { None, Quote, Code }

    public KnownLayer Layer => KnownLayer.Background;

    public void Draw(TextView textView, DrawingContext context)
    {
        if (editor.IsSourceMode || !textView.VisualLinesValid)
            return;
        var model = editor.Model;
        var width = textView.Bounds.Width;
        var offsetY = textView.VerticalOffset;

        // 编辑中的引用消息：从标题栏到围栏结束是一张卡片（与展开的面板相同），其中的引用不再画底色
        var editing = editor.EditingQuote;
        if (editing is not null)
        {
            double cardTop = double.NaN, cardBottom = 0;
            foreach (var visualLine in textView.VisualLines)
            {
                var index = visualLine.FirstDocumentLine.LineNumber - 1;
                if (index < editing.HeaderLine || index > editing.CloseLine)
                    continue;
                var y = visualLine.VisualTop - offsetY;
                if (double.IsNaN(cardTop))
                    cardTop = y;
                cardBottom = y + visualLine.Height;
            }
            if (!double.IsNaN(cardTop))
                DrawBlock(context, BlockKind.Quote, cardTop, cardBottom, width);
        }

        var bars = new List<Rect>();
        var kind = BlockKind.None;
        double top = 0, bottom = 0;
        foreach (var visualLine in textView.VisualLines)
        {
            var index = visualLine.FirstDocumentLine.LineNumber - 1;
            var line = index < model.Lines.Count ? model.Lines[index] : null;
            var y = visualLine.VisualTop - offsetY;
            var inCard = editing is not null && editing.HeaderLine <= index && index <= editing.CloseLine;
            var lineKind = line is null ? BlockKind.None : inCard && KindOf(line) == BlockKind.Quote ? BlockKind.None : KindOf(line);
            if (lineKind != kind || lineKind == BlockKind.None)
            {
                DrawBlock(context, kind, top, bottom, width);
                kind = lineKind;
                top = y;
            }
            bottom = y + visualLine.Height;

            if (line is not null && !editor.IsLineRevealed(index) && line.Prefixes.Any(p => p.Kind == MdPrefixKind.Quote))
                bars.Add(new Rect(0, y, BarWidth, visualLine.Height));
            if (line is { Kind: MdLineKind.Rule } && !editor.IsLineRevealed(index))
            {
                var middle = y + visualLine.Height / 2;
                context.FillRectangle(editor.Brush("DividerBrush"), new Rect(0, middle, width, 1));
            }
        }
        DrawBlock(context, kind, top, bottom, width);
        if (bars.Count > 0)
        {
            var barBrush = editor.Brush("QuoteBarBrush");
            foreach (var bar in bars)
                context.FillRectangle(barBrush, bar);
        }
    }

    private static BlockKind KindOf(MdLine line)
    {
        if (line.QuoteDepth > 0)
            return BlockKind.Quote;
        if (line.Kind == MdLineKind.IndentedCode || line.Fence is MdFenceKind.Code or MdFenceKind.Math or MdFenceKind.Spoiler)
            return BlockKind.Code;
        return BlockKind.None;
    }

    private void DrawBlock(DrawingContext context, BlockKind kind, double top, double bottom, double width)
    {
        if (kind == BlockKind.None || bottom <= top || width <= 0)
            return;
        var rect = new Rect(0, top, width, bottom - top);
        context.DrawRectangle(editor.Brush(kind == BlockKind.Quote ? "QuoteCardBrush" : "FillBrush"), null, rect, Radius, Radius);
    }
}
