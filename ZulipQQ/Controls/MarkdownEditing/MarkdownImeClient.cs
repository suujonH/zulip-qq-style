// 移植自 AvaloniaEdit（src/AvaloniaEdit/Editing/TextArea.cs 中的私有类 TextAreaTextInputMethodClient），
// 支持组字显示（SupportsPreedit）。原代码：
// Copyright (c) 2014 AlphaSierraPapa for the SharpDevelop Team
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System;
using Avalonia;
using Avalonia.Input.TextInput;
using AvaloniaEdit;
using AvaloniaEdit.Editing;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 输入法客户端：与 AvaloniaEdit 自带的相同，但支持组字显示——组字文字交给 <see cref="MarkdownEditor"/>
/// 在光标处覆盖显示（<see cref="PreeditLayer"/>），不进入文档，也不进入撤销记录。
/// </summary>
internal sealed class MarkdownImeClient(MarkdownEditor editor) : TextInputMethodClient
{
    private TextArea? _textArea;

    public override Rect CursorRectangle
    {
        get
        {
            if (_textArea is null)
                return default;
            var transform = _textArea.TextView.TransformToVisual(_textArea);
            if (transform is null)
                return default;
            var rect = _textArea.Caret.CalculateCaretRectangle().TransformToAABB(transform.Value);
            var scrollOffset = _textArea.TextView.ScrollOffset;
            return rect.WithX(rect.X - scrollOffset.X).WithY(rect.Y - scrollOffset.Y);
        }
    }

    public override Visual TextViewVisual => _textArea!;

    public override bool SupportsPreedit => true;

    public override bool SupportsSurroundingText => true;

    public override string SurroundingText
    {
        get
        {
            if (_textArea is null)
                return "";
            var line = _textArea.Document.GetLineByNumber(_textArea.Caret.Line);
            return _textArea.Document.GetText(line.Offset, line.Length);
        }
    }

    public override TextSelection Selection
    {
        get
        {
            if (_textArea is null)
                return new TextSelection(0, 0);
            return new TextSelection(_textArea.Caret.Position.Column, _textArea.Caret.Position.Column + _textArea.Selection.Length);
        }
        set
        {
            if (_textArea is null)
                return;
            var selection = _textArea.Selection;
            if (selection.StartPosition.Line == 0)
                return;
            _textArea.Selection = selection.StartSelectionOrSetEndpoint(
                new TextViewPosition(selection.StartPosition.Line, value.Start),
                new TextViewPosition(selection.StartPosition.Line, value.End));
        }
    }

    /// <summary>编辑区获得焦点时连接，失去焦点时传 null 断开。</summary>
    public void Attach(TextArea? textArea)
    {
        if (_textArea is not null)
            _textArea.Caret.PositionChanged -= OnCaretPositionChanged;
        _textArea = textArea;
        if (_textArea is not null)
            _textArea.Caret.PositionChanged += OnCaretPositionChanged;
        RaiseTextViewVisualChanged();
        RaiseCursorRectangleChanged();
        RaiseSurroundingTextChanged();
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        RaiseCursorRectangleChanged();
        RaiseSurroundingTextChanged();
        RaiseSelectionChanged();
    }

    public override void SetPreeditText(string? preeditText) => editor.SetPreedit(preeditText);

    public override void SetPreeditText(string? preeditText, int? cursorPos) => editor.SetPreedit(preeditText);
}
