using Avalonia.Media.TextFormatting;
using AvaloniaEdit.Rendering;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 单元格中断行的位置（见 <see cref="MarkdownTableWrap"/>），文档不变。整个元素是一个整体，光标只停在两端：
/// 空格不显示，光标从上一行的末尾一步到下一行的开头，退格、Delete 删除它；符号照常显示，选择、删除时是一个字符。
/// </summary>
internal sealed class SoftBreakElement(SoftBreakKind kind, string text, int documentLength)
    : VisualLineElement(kind == SoftBreakKind.Space ? 1 : 2, documentLength)
{
    public override TextRun CreateTextRun(int startVisualColumn, ITextRunConstructionContext context)
    {
        var relative = startVisualColumn - VisualColumn;
        var isCharacter = kind switch
        {
            SoftBreakKind.After => relative == 0,
            SoftBreakKind.Before => relative == 1,
            _ => false,
        };
        return isCharacter ? new TextCharacters(text, TextRunProperties) : new TextEndOfLine(1);
    }
}
