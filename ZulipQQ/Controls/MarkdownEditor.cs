using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Input.TextInput;
using Avalonia.Interactivity;
using Avalonia.Media;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;
using ZulipQQ.Controls.MarkdownEditing;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.Controls;

/// <summary>
/// 输入框：所见即所得的 Markdown 编辑器。文档内容始终是 Markdown 原文（撤销、复制、草稿、发送都直接用原文），
/// 显示时按 Zulip 服务器的规则（<see cref="ZulipMarkdown"/>）渲染格式：符号平时隐藏，光标进入代码、链接、粗体、斜体时显示；
/// 提及、频道链接、表情、时间、图片始终作为一个整体。<see cref="IsSourceMode"/> 为 true 时显示原文（只给符号上色）。
/// </summary>
public class MarkdownEditor : TextEditor
{
    public static readonly StyledProperty<string> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownEditor, string>(nameof(Markdown), "", defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<bool> IsSourceModeProperty =
        AvaloniaProperty.Register<MarkdownEditor, bool>(nameof(IsSourceMode));

    private static readonly IMarkdownResolver NullResolver = new EmptyResolver();

    private readonly PreeditLayer _preeditLayer;
    private readonly MarkdownImeClient _ime;
    private IMarkdownResolver? _resolver;
    private bool _syncing;
    private int _revealLine = -1;
    private IReadOnlyList<MdSpan> _revealSpans = [];
    private MdSpan? _pressedAtom;

    public MarkdownEditor()
    {
        WordWrap = true;
        ShowLineNumbers = false;
        HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled;
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto;
        Background = Brushes.Transparent;
        Options.EnableHyperlinks = false;
        Options.EnableEmailHyperlinks = false;
        Options.ConvertTabsToSpaces = true;
        Options.IndentationSize = 2;
        Options.HighlightCurrentLine = false;
        Options.AllowScrollBelowDocument = false;
        Options.EnableRectangularSelection = false;
        Options.EnableTextDragDrop = false;
        // 搜索面板（Ctrl+F）在输入框中没有用
        SearchPanel?.Uninstall();
        // 拖放文件由聊天窗口处理
        DragDrop.SetAllowDrop(TextArea, false);

        // 选中的文字为白色（选中背景是主题色，主题色的提及、链接会看不清）。
        // 着色放在最前面，TextArea 自带的选中着色在它之后执行，覆盖选中部分的颜色
        TextArea.SelectionForeground = Brushes.White;
        var textView = TextArea.TextView;
        textView.LineTransformers.Insert(0, new MarkdownColorizer(this));
        textView.ElementGenerators.Add(new MarkdownElementGenerator(this));
        textView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(this));
        _preeditLayer = new PreeditLayer(this);
        textView.InsertLayer(_preeditLayer, KnownLayer.Caret, LayerInsertionPosition.Above);
        _ime = new MarkdownImeClient(this);

        TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        TextArea.GotFocus += (_, _) =>
        {
            _ime.Attach(TextArea);
            RefreshReveal();
        };
        TextArea.LostFocus += (_, _) =>
        {
            _ime.Attach(null);
            SetPreedit(null);
            RefreshReveal();
        };
        // TextArea 在类处理器中设置自己的输入法客户端（不支持组字显示），这里在冒泡阶段换成自己的
        AddHandler(TextInputMethodClientRequestedEvent, OnImeClientRequested, RoutingStrategies.Bubble, handledEventsToo: true);
        // 换行统一为 \n；输入区的发送快捷键在外层先处理
        TextArea.TextEntering += OnTextEntering;
        AddHandler(PointerPressedEvent, OnPointerPressedTunnel, RoutingStrategies.Tunnel);
        AddHandler(PointerReleasedEvent, OnPointerReleasedAny, RoutingStrategies.Bubble, handledEventsToo: true);
        ActualThemeVariantChanged += (_, _) => TextArea.TextView.Redraw();
    }

    protected override Type StyleKeyOverride => typeof(TextEditor);

    /// <summary>Markdown 原文（双向绑定）。</summary>
    public string Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public bool IsSourceMode
    {
        get => GetValue(IsSourceModeProperty);
        set => SetValue(IsSourceModeProperty, value);
    }

    /// <summary>判断提及、频道、表情是否存在；为 null 时都不存在。</summary>
    public IMarkdownResolver? Resolver
    {
        get => _resolver;
        set
        {
            _resolver = value;
            Reparse();
            TextArea.TextView.Redraw();
        }
    }

    /// <summary>按名称取表情（显示 Unicode 字符或组织表情的图片）。</summary>
    public Func<string, EmojiEntry?>? EmojiLookup { get; set; }

    /// <summary>最近一次解析结果，与文档内容一致。</summary>
    public MarkdownModel Model { get; private set; } = MarkdownModel.Empty;

    /// <summary>光标位置；设置时清除选择。</summary>
    public int CaretIndex
    {
        get => CaretOffset;
        set
        {
            TextArea.ClearSelection();
            CaretOffset = Math.Clamp(value, 0, Document.TextLength);
        }
    }

    /// <summary>选择的结束位置；没有选择时等于光标位置（与 <see cref="TextEditor.SelectionStart"/> 相同）。</summary>
    public int SelectionEnd => SelectionStart + SelectionLength;

    /// <summary>提及的文字换成 <see cref="MentionText.MaskChar"/> 的原文，用于 @ 补全（提及中的 @ 不触发补全）。</summary>
    public string MaskedText
    {
        get
        {
            var text = Text;
            return MentionText.Mask(text, ZulipMarkdown.MentionSpans(Model, text));
        }
    }

    /// <summary>输入法正在组字的文字（还没有进入文档）。</summary>
    public string? PreeditText { get; private set; }

    public bool HasPreedit => !string.IsNullOrEmpty(PreeditText);

    /// <summary>光标位置变化。</summary>
    public event EventHandler? CaretMoved;

    /// <summary>输入法组字文字变化。</summary>
    public event EventHandler? PreeditChanged;

    /// <summary>用 <paramref name="markdown"/> 替换 <c>[start, end)</c>，光标移到插入内容之后。</summary>
    public void Replace(int start, int end, string markdown)
    {
        var length = Document.TextLength;
        start = Math.Clamp(start, 0, length);
        end = Math.Clamp(end, start, length);
        markdown = NormalizeNewLines(markdown);
        Document.Replace(start, end - start, markdown);
        CaretIndex = start + markdown.Length;
        TextArea.Caret.BringCaretToView();
    }

    /// <summary>在光标处插入 <paramref name="markdown"/>（替换选中部分）。</summary>
    public void Insert(string markdown) => Replace(SelectionStart, SelectionEnd, markdown);

    /// <summary>插入粘贴等放入的内容：多行时结尾补一个换行（见 <see cref="MarkdownFormat.WithTrailingLine"/>）。</summary>
    public void InsertBlock(string markdown) => Insert(MarkdownFormat.WithTrailingLine(markdown));

    /// <summary>
    /// 复制选中的部分：Markdown 原文（应用内粘贴保留格式）与显示的文字（其它程序得到它；源码模式下显示的就是原文）。
    /// <paramref name="cut"/> 为 true 时复制后删除选中部分。
    /// </summary>
    public async Task CopyAsync(bool cut)
    {
        if (SelectionLength == 0 || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var start = SelectionStart;
        var markdown = SelectedText;
        var text = IsSourceMode
            ? markdown
            : ZulipMarkdown.ToPlainText(markdown, _resolver ?? NullResolver, name => EmojiLookup?.Invoke(name)?.Text);
        try
        {
            await MarkdownClipboard.SetAsync(clipboard, text, markdown);
        }
        catch (Exception ex)
        {
            Log.Warn("复制失败", ex);
            return;
        }
        // 复制期间选择没有变化才删除
        if (cut && !IsReadOnly && SelectionStart == start && SelectedText == markdown)
            Replace(start, start + markdown.Length, "");
    }

    /// <summary>粘贴剪贴板中的文字（换行统一为 \n），不使用复制时附带的格式。</summary>
    public new async void Paste()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪贴板失败", ex);
            return;
        }
        if (!string.IsNullOrEmpty(text) && !IsReadOnly)
            InsertBlock(text.ReplaceLineEndings("\n"));
    }

    /// <summary>格式按钮：一步修改，可以一次撤销。</summary>
    public void ApplyFormat(MarkdownFormatKind kind) =>
        ApplyEdit(MarkdownFormat.Apply(Text, SelectionStart, SelectionEnd, kind, Model));

    /// <summary>把选中的行（或光标所在行）设为标题，0 为普通段落。</summary>
    public void SetHeading(int level) =>
        ApplyEdit(MarkdownFormat.SetHeading(Text, SelectionStart, SelectionEnd, level));

    /// <summary>光标处是否已有该格式。</summary>
    public bool IsFormatActive(MarkdownFormatKind kind) => MarkdownFormat.IsActive(Model, CaretIndex, kind);

    /// <summary>光标所在行的标题级别；不是标题为 0。</summary>
    public int HeadingLevel => MarkdownFormat.HeadingLevelAt(Model, CaretIndex);

    /// <summary>插入全局时间。</summary>
    public void InsertTime(DateTimeOffset time) =>
        ApplyEdit(MarkdownFormat.InsertTime(Text, SelectionStart, SelectionEnd, time));

    /// <summary>清空撤销记录（发送、载入草稿、切换编辑的消息之后）。</summary>
    public void ResetHistory() => Document.UndoStack.ClearAll();

    /// <summary>应用一次修改（一步撤销）并设置选择。</summary>
    public void ApplyEdit(MarkdownEdit edit)
    {
        Document.Replace(edit.Start, edit.Length, edit.Replacement);
        if (edit.SelectionStart == edit.SelectionEnd)
            CaretIndex = edit.SelectionStart;
        else
            Select(edit.SelectionStart, edit.SelectionEnd - edit.SelectionStart);
        TextArea.Caret.BringCaretToView();
    }

    // ───────────── 同步 ─────────────

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty && !_syncing)
            SyncFromMarkdown(change.GetNewValue<string?>() ?? "");
        else if (change.Property == IsSourceModeProperty)
            TextArea.TextView.Redraw();
    }

    // ViewModel 改了文字（载入草稿、上传完成替换占位）：只替换不同的部分，光标与撤销不乱
    private void SyncFromMarkdown(string value)
    {
        var text = NormalizeNewLines(value);
        var old = Document.Text;
        _syncing = true;
        try
        {
            if (text != old)
            {
                var prefix = 0;
                var max = Math.Min(old.Length, text.Length);
                while (prefix < max && old[prefix] == text[prefix])
                    prefix++;
                var suffix = 0;
                while (suffix < max - prefix && old[^(suffix + 1)] == text[^(suffix + 1)])
                    suffix++;
                Document.Replace(prefix, old.Length - prefix - suffix, text.Substring(prefix, text.Length - prefix - suffix));
                // 占位换成更长的内容（引用块）后光标会被挤出可见区域：正在输入时让光标保持可见
                if (IsKeyboardFocusWithin)
                    TextArea.Caret.BringCaretToView();
            }
            if (text != value)
                SetCurrentValue(MarkdownProperty, text);
        }
        finally
        {
            _syncing = false;
        }
    }

    // 先解析再触发 TextChanged：事件处理中会用到 Model（MaskedText）
    protected override void OnTextChanged(EventArgs e)
    {
        Reparse();
        if (!_syncing)
        {
            _syncing = true;
            try
            {
                SetCurrentValue(MarkdownProperty, Document.Text);
            }
            finally
            {
                _syncing = false;
            }
        }
        RefreshReveal();
        TextArea.TextView.Redraw();
        base.OnTextChanged(e);
    }

    private void Reparse() => Model = ZulipMarkdown.Parse(Document.Text, _resolver ?? NullResolver);

    private static string NormalizeNewLines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    // ───────────── 揭示（光标进入时显示符号） ─────────────

    /// <summary>光标所在的代码块围栏行（显示围栏原文）；没有焦点或光标不在围栏行上时为 -1。</summary>
    internal bool IsLineRevealed(int lineIndex) => lineIndex == _revealLine;

    /// <summary>光标位于其中（含两端）的代码、链接、粗体、斜体显示符号。</summary>
    internal bool IsRevealed(MdSpan span) => _revealSpans.Contains(span);

    // 只有代码、链接、粗体、斜体显示符号；标题、列表、引用的行首符号、删除线、公式、提及等始终是渲染后的样子
    private static bool Reveals(MdSpanKind kind) =>
        kind is MdSpanKind.Code or MdSpanKind.Link or MdSpanKind.Url or MdSpanKind.Strong or MdSpanKind.Em or MdSpanKind.StrongEm;

    private static bool RevealsLine(MdLine line) =>
        line is { Kind: MdLineKind.FenceOpen or MdLineKind.FenceClose, Fence: MdFenceKind.Code };

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        RefreshReveal();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshReveal()
    {
        int line;
        IReadOnlyList<MdSpan> spans;
        if (TextArea.IsKeyboardFocusWithin)
        {
            var caret = CaretOffset;
            line = Model.LineIndexAt(caret);
            if (line >= 0 && !RevealsLine(Model.Lines[line]))
                line = -1;
            spans = Model.Spans.Where(s => Reveals(s.Kind) && s.Start <= caret && caret <= s.End).ToList();
        }
        else
        {
            line = -1;
            spans = [];
        }
        if (line == _revealLine && spans.SequenceEqual(_revealSpans))
            return;
        _revealLine = line;
        _revealSpans = spans;
        TextArea.TextView.Redraw();
    }

    // ───────────── 输入 ─────────────

    // 换行由 TextArea 换成文档的换行符（文档只有一行时为系统换行符 \r\n），这里直接插入 \n
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is not ("\n" or "\r" or "\r\n"))
            return;
        e.Handled = true;
        TextArea.Selection.ReplaceSelectionWithText("\n");
        TextArea.Caret.BringCaretToView();
    }

    // 单击提及、频道链接、表情、图片等整体元素：松开后选中整个元素
    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _pressedAtom = null;
        var point = e.GetCurrentPoint(TextArea.TextView);
        if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 1 || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        _pressedAtom = AtomAt(point.Position);
    }

    private void OnPointerReleasedAny(object? sender, PointerReleasedEventArgs e)
    {
        if (_pressedAtom is not { } atom)
            return;
        _pressedAtom = null;
        if (e.InitialPressMouseButton != MouseButton.Left || atom.End > Document.TextLength)
            return;
        if (AtomAt(e.GetPosition(TextArea.TextView)) == atom)
            Select(atom.Start, atom.End - atom.Start);
    }

    private MdSpan? AtomAt(Point point)
    {
        var textView = TextArea.TextView;
        if (IsSourceMode || textView.GetPositionFloor(point + textView.ScrollOffset) is not { } position)
            return null;
        var offset = Document.GetOffset(position.Location);
        return Model.Spans.FirstOrDefault(s => ZulipMarkdown.IsAtomic(s.Kind) && s.Start == offset);
    }

    private void OnImeClientRequested(object? sender, TextInputMethodClientRequestedEventArgs e)
    {
        if (!IsReadOnly && IsEnabled)
            e.Client = _ime;
    }

    internal void SetPreedit(string? text)
    {
        if (text == PreeditText || string.IsNullOrEmpty(text) && string.IsNullOrEmpty(PreeditText))
            return;
        PreeditText = text;
        _preeditLayer.InvalidateVisual();
        PreeditChanged?.Invoke(this, EventArgs.Empty);
    }

    // ───────────── 资源 ─────────────

    internal IBrush Brush(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : Brushes.Gray;

    internal double Size(string key) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is double size ? size : FontSize;

    private sealed class EmptyResolver : IMarkdownResolver
    {
        public bool HasUser(string name, long? id) => false;
        public bool HasGroup(string name) => false;
        public bool HasChannel(string name) => false;
        public bool HasEmoji(string name) => false;
    }
}
