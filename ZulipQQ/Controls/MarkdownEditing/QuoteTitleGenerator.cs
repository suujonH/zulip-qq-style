using System;
using Avalonia.Controls;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 正在编辑的引用消息（<see cref="MarkdownEditor.EditingQuote"/>）：在引用行的开头放标题栏（<see cref="QuotePanel"/>），
/// 宽度占满一行，引用行的文字换到下一行，看起来是面板展开后在其中编辑。
/// 标题栏不对应原文（文档长度为 0）。与 <see cref="MarkdownElementGenerator"/> 分开：同一位置还有提及，
/// 一个生成器在一个位置只生成一个元素。
/// </summary>
internal sealed class QuoteTitleGenerator(MarkdownEditor editor) : VisualLineElementGenerator
{
    private QuoteBlock? _quote;

    public override void StartGeneration(ITextRunConstructionContext context)
    {
        base.StartGeneration(context);
        var line = context.VisualLine.FirstDocumentLine;
        _quote = !editor.IsSourceMode && editor.EditingQuote is { } quote && quote.Start == line.Offset ? quote : null;
    }

    public override int GetFirstInterestedOffset(int startOffset) =>
        _quote is { } quote && quote.Start >= startOffset ? quote.Start : -1;

    public override VisualLineElement? ConstructElement(int offset)
    {
        if (_quote is not { } quote || offset != quote.Start)
            return null;
        var width = Math.Max(120, CurrentContext.TextView.Bounds.Width - 4);
        return new TitleElement(new QuotePanel(editor, quote, width, editing: true));
    }

    // 光标不停在标题栏的前面（与引用行开头是同一个文档位置）
    private sealed class TitleElement(Control panel) : InlineObjectElement(0, panel)
    {
        public override int GetNextCaretPosition(int visualColumn, LogicalDirection direction, CaretPositioningMode mode) => -1;
    }
}
