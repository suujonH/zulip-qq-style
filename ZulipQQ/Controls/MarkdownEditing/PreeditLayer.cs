using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 输入法组字文字：在光标处覆盖显示（先用背景色盖住后面的文字），底部加下划线。不推开后面的文字。
/// </summary>
internal sealed class PreeditLayer : Control
{
    private readonly MarkdownEditor _editor;

    public PreeditLayer(MarkdownEditor editor)
    {
        _editor = editor;
        IsHitTestVisible = false;
    }

    public override void Render(DrawingContext context)
    {
        if (_editor.PreeditText is not { Length: > 0 } text)
            return;
        var textView = _editor.TextArea.TextView;
        var caret = _editor.TextArea.Caret.CalculateCaretRectangle();
        var origin = new Point(caret.X - textView.ScrollOffset.X, caret.Y - textView.ScrollOffset.Y);
        var foreground = _editor.Foreground ?? Brushes.Black;
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(_editor.FontFamily, _editor.FontStyle, _editor.FontWeight), _editor.FontSize, foreground);
        var height = System.Math.Max(caret.Height, formatted.Height);
        context.FillRectangle(BackgroundOf(_editor), new Rect(origin.X, origin.Y, formatted.WidthIncludingTrailingWhitespace, height));
        context.DrawText(formatted, origin);
        context.FillRectangle(foreground, new Rect(origin.X, origin.Y + height - 1, formatted.WidthIncludingTrailingWhitespace, 1));
    }

    // 输入框本身是透明的：取外层第一个不透明的背景
    private static IBrush BackgroundOf(Visual visual)
    {
        foreach (var ancestor in visual.GetSelfAndVisualAncestors())
        {
            var brush = ancestor switch
            {
                Panel p => p.Background,
                Border b => b.Background,
                Avalonia.Controls.Primitives.TemplatedControl t => t.Background,
                _ => null,
            };
            if (brush is ISolidColorBrush { Color.A: > 0 } or IGradientBrush)
                return brush;
        }
        return Brushes.White;
    }
}
