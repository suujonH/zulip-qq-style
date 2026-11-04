using System;
using System.Collections.Generic;
using System.Globalization;
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
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
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
    private int _revealQuote = -1;
    // 展开的引用消息：锚点在引用的开头，跟随前面的修改移动
    private readonly List<TextAnchor> _expandedQuotes = [];
    // 显示为面板的引用消息除第一行以外的行：TextView 要求被跨行元素盖住的行先折叠
    private readonly List<CollapsedLineSection> _collapsedQuotes = [];
    // 点编辑进入编辑的引用（锚点在引用的开头）；光标离开引用后清除。其它引用不能编辑
    private TextAnchor? _editQuote;
    private int _animateQuote = -1;
    // 引用消息发送后的 HTML：按引用的原文缓存，正在获取为 null，获取失败为空字符串
    private readonly Dictionary<string, string?> _renderedQuotes = [];
    // 表格的排版（键为标题行）：文字或字号变化时重新计算
    private readonly Dictionary<int, MarkdownTableLayout> _tableLayouts = [];
    private double _tableLayoutFontSize;
    private readonly MarkdownTableToolbarLayer? _tableToolbarLayer;
    // 光标所在的表格（显示工具栏）的标题行；没有为 -1
    private int _activeTableLine = -1;
    // 工具栏的菜单打开时焦点不在编辑器上，工具栏仍然显示
    private bool _tablePopupOpen;
    private MdSpan? _pressedAtom;

    public MarkdownEditor() : base(new GuardedTextArea())
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
        // 标题栏是宽度为 0 的元素，要在同一位置的提及之前生成
        textView.ElementGenerators.Add(new QuoteTitleGenerator(this));
        textView.ElementGenerators.Add(new MarkdownElementGenerator(this));
        textView.BackgroundRenderers.Add(new MarkdownBackgroundRenderer(this));
        // 引用面板的宽度跟随输入框（第一次生成时输入框可能还没有宽度）；表格放不放得下也取决于宽度
        textView.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && (Quotes.Count > 0 || Tables.Count > 0))
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateQuoteCollapse();
                    textView.Redraw();
                });
        };
        _tableToolbarLayer = new MarkdownTableToolbarLayer(this);
        textView.InsertLayer(_tableToolbarLayer, KnownLayer.Caret, LayerInsertionPosition.Above);
        textView.VisualLinesChanged += (_, _) => _tableToolbarLayer?.Update();
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

    /// <summary>把 Markdown 渲染为发送后的 HTML（展开的引用显示发送后的样子）；为 null 时显示纯文字。</summary>
    public Func<string, Task<string>>? RenderHtml { get; set; }

    /// <summary>最近一次解析结果，与文档内容一致。</summary>
    public MarkdownModel Model { get; private set; } = MarkdownModel.Empty;

    /// <summary>正文中的引用消息（渲染模式下显示为收起的面板，见 <see cref="QuotePanel"/>）。</summary>
    public IReadOnlyList<QuoteBlock> Quotes { get; private set; } = [];

    /// <summary>文档中的表格（渲染模式下对齐显示，见 <see cref="TableAt"/>）。</summary>
    public IReadOnlyList<MarkdownTable> Tables { get; private set; } = [];

    /// <summary>点击引用面板的跳转：参数为被引用的消息 id。</summary>
    public event EventHandler<long>? QuoteJumpRequested;

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
        var text = IsSourceMode ? markdown : PlainText(markdown);
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
        {
            text = text.ReplaceLineEndings("\n");
            if (!InsertBesideQuote(text))
                InsertBlock(text);
        }
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
        {
            UpdateQuoteCollapse();
            TextArea.TextView.Redraw();
        }
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
        UpdateQuoteCollapse();
        TextArea.TextView.Redraw();
        base.OnTextChanged(e);
    }

    private void Reparse()
    {
        var text = Document.Text;
        Model = ZulipMarkdown.Parse(text, _resolver ?? NullResolver);
        Quotes = QuoteBlocks.Find(Model, text);
        Tables = MarkdownTables.Find(Model);
        _tableLayouts.Clear();
    }

    /// <summary>
    /// <paramref name="lineIndex"/> 所在的、按表格显示的表格与它的排版。
    /// 源码模式下，或表格比输入框宽（换行后无法对齐，按原文显示）时为 null。
    /// </summary>
    internal (MarkdownTable Table, MarkdownTableLayout Layout)? TableAt(int lineIndex)
    {
        if (IsSourceMode)
            return null;
        foreach (var table in Tables)
        {
            if (lineIndex < table.HeaderLine || lineIndex > table.LastLine)
                continue;
            return LayoutOf(table) is { } layout ? (table, layout) : null;
        }
        return null;
    }

    /// <summary>光标所在的、显示工具栏的表格。</summary>
    internal (MarkdownTable Table, MarkdownTableLayout Layout)? ActiveTable =>
        _activeTableLine < 0 ? null : TableAt(_activeTableLine);

    /// <summary>当前列的对齐方式。</summary>
    internal TableAlign ActiveTableAlign
    {
        get
        {
            if (ActiveTable is not { } active)
                return TableAlign.Left;
            var text = Document.Text;
            var aligns = MarkdownTableEdit.Aligns(Model, text, active.Table);
            var column = MarkdownTableEdit.CellAt(Model, text, active.Table, CaretOffset).Column;
            return column < aligns.Count ? aligns[column] : TableAlign.Left;
        }
    }

    /// <summary>当前表格的列数与行数（含标题行）。</summary>
    internal (int Columns, int Rows) ActiveTableSize =>
        ActiveTable is { } active
            ? (MarkdownTableEdit.ColumnCount(Model, Document.Text, active.Table), MarkdownTableEdit.RowCount(active.Table))
            : (MarkdownTableEdit.MinColumns, MarkdownTableEdit.MinRows);

    /// <summary>修改当前表格（一步撤销）；操作不了（返回 null）时不改。之后焦点回到编辑器。</summary>
    internal void EditActiveTable(Func<MarkdownModel, string, MarkdownTable, int, MarkdownEdit?> edit)
    {
        if (ActiveTable is { } active && !IsReadOnly && edit(Model, Document.Text, active.Table, CaretOffset) is { } change)
            ApplyEdit(change);
        TextArea.Focus();
    }

    /// <summary>复制当前表格的原文。</summary>
    internal async Task CopyActiveTableAsync()
    {
        if (ActiveTable is not { } active || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var source = MarkdownTableEdit.Source(Model, Document.Text, active.Table);
        await MarkdownClipboard.SetAsync(clipboard, source, source);
        TextArea.Focus();
    }

    internal void SetTablePopupOpen(bool open)
    {
        if (open)
        {
            _tablePopupOpen = true;
            return;
        }
        // 菜单项的操作可能在关闭之后才执行：等它执行完
        Dispatcher.UIThread.Post(() =>
        {
            _tablePopupOpen = false;
            TextArea.Focus();
            RefreshReveal();
        });
    }

    /// <summary>插入空表格，光标放在第一个单元格。</summary>
    public void InsertTable(int columns, int rows) =>
        ApplyEdit(MarkdownFormat.InsertTable(Text, SelectionStart, SelectionEnd, columns, rows));

    /// <summary>表格的快捷键（光标在表格中时）；处理了返回 true。</summary>
    public bool HandleTableKey(KeyEventArgs e)
    {
        if (ActiveTable is null)
            return false;
        Func<MarkdownModel, string, MarkdownTable, int, MarkdownEdit?>? edit = (e.KeyModifiers, e.Key) switch
        {
            (KeyModifiers.Control, Key.Enter) => (m, t, table, c) => MarkdownTableEdit.InsertRow(m, t, table, c, below: true),
            (KeyModifiers.Alt, Key.Up) => (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, -1),
            (KeyModifiers.Alt, Key.Down) => (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, 1),
            (KeyModifiers.Alt, Key.Left) => (m, t, table, c) => MarkdownTableEdit.MoveColumn(m, t, table, c, -1),
            (KeyModifiers.Alt, Key.Right) => (m, t, table, c) => MarkdownTableEdit.MoveColumn(m, t, table, c, 1),
            (KeyModifiers.Control | KeyModifiers.Shift, Key.Back) => MarkdownTableEdit.DeleteRow,
            _ => null,
        };
        if (edit is null)
            return false;
        e.Handled = true;
        EditActiveTable(edit);
        return true;
    }

    private MarkdownTableLayout? LayoutOf(MarkdownTable table)
    {
        var view = TextArea.TextView;
        if (_tableLayoutFontSize != FontSize)
        {
            _tableLayouts.Clear();
            _tableLayoutFontSize = FontSize;
        }
        if (!_tableLayouts.TryGetValue(table.HeaderLine, out var layout))
        {
            layout = MarkdownTableLayout.Build(table, Model, Document.Text, MeasureTableCell, view.DefaultLineHeight, view.DefaultBaseline);
            _tableLayouts[table.HeaderLine] = layout;
        }
        return layout.Width <= view.Bounds.Width ? layout : null;
    }

    // 单元格显示的宽度：符号隐藏、提及等换成显示文字；两端的空白照常占位
    private double MeasureTableCell(string cell, bool bold)
    {
        var trimmed = cell.Trim();
        var shown = trimmed.Length == 0 ? cell
            : cell[..(cell.Length - cell.TrimStart().Length)] + PlainText(trimmed) + cell[(cell.TrimEnd().Length)..];
        return new FormattedText(shown, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, bold ? FontWeight.Bold : FontWeight.Normal), FontSize, null)
            .WidthIncludingTrailingWhitespace;
    }

    /// <summary>Markdown 显示的文字（提及、表情等换成显示的样子）。</summary>
    internal string PlainText(string markdown) =>
        ZulipMarkdown.ToPlainText(markdown, _resolver ?? NullResolver, name => EmojiLookup?.Invoke(name)?.Text);

    private static string NormalizeNewLines(string text) =>
        text.Contains('\r') ? text.Replace("\r\n", "\n").Replace('\r', '\n') : text;

    // ───────────── 揭示（光标进入时显示符号） ─────────────

    /// <summary>光标所在的代码块围栏行（显示围栏原文）；没有焦点或光标不在围栏行上时为 -1。</summary>
    internal bool IsLineRevealed(int lineIndex) => lineIndex == _revealLine;

    /// <summary>光标位于其中（含两端）的代码、链接、粗体、斜体显示符号。</summary>
    internal bool IsRevealed(MdSpan span) => _revealSpans.Contains(span);

    /// <summary>光标在引用消息中间（不含两端）：显示原文，可以编辑；光标离开后收起为面板。</summary>
    internal bool IsQuoteRevealed(QuoteBlock quote) => quote.Start == _revealQuote;

    internal bool IsQuoteExpanded(QuoteBlock quote) => _expandedQuotes.Exists(a => !a.IsDeleted && a.Offset == quote.Start);

    /// <summary>正在编辑（显示原文）的引用消息；没有为 null。</summary>
    internal QuoteBlock? EditingQuote => _revealQuote < 0 ? null : Quotes.FirstOrDefault(q => q.Start == _revealQuote);

    /// <summary>
    /// 引用消息发送后的 HTML（整段，含引用行）。第一次取时开始获取，取到后重绘；
    /// 正在获取、获取失败或没有 <see cref="RenderHtml"/> 时为 null。
    /// </summary>
    internal string? RenderedQuote(QuoteBlock quote)
    {
        if (RenderHtml is not { } render || quote.End > Document.TextLength)
            return null;
        var text = Document.GetText(quote.Start, quote.End - quote.Start);
        if (_renderedQuotes.TryGetValue(text, out var html))
            return string.IsNullOrEmpty(html) ? null : html;
        // 只缓存最近的几段，修改过的旧内容不再需要
        if (_renderedQuotes.Count >= 32)
            _renderedQuotes.Clear();
        _renderedQuotes[text] = null;
        _ = RenderQuoteAsync(render, text);
        return null;
    }

    private async Task RenderQuoteAsync(Func<string, Task<string>> render, string text)
    {
        string html;
        try
        {
            html = await render(text);
        }
        catch (Exception ex)
        {
            Log.Warn("渲染引用失败", ex);
            html = "";
        }
        _renderedQuotes[text] = html;
        if (html.Length > 0)
            TextArea.TextView.Redraw();
    }

    // 与 MarkdownElementGenerator 生成面板的条件一致：渲染模式下、没有在编辑的引用折叠第一行以外的行
    private void UpdateQuoteCollapse()
    {
        foreach (var section in _collapsedQuotes)
            section.Uncollapse();
        _collapsedQuotes.Clear();
        if (IsSourceMode)
            return;
        foreach (var quote in Quotes)
        {
            if (IsQuoteRevealed(quote) || quote.CloseLine + 1 > Document.LineCount)
                continue;
            _collapsedQuotes.Add(TextArea.TextView.CollapseLines(
                Document.GetLineByNumber(quote.HeaderLine + 2), Document.GetLineByNumber(quote.CloseLine + 1)));
        }
        // 表格的分隔行不显示（在 MarkdownElementGenerator 中并入标题行）
        foreach (var table in Tables)
        {
            if (table.SeparatorLine + 1 > Document.LineCount || LayoutOf(table) is null)
                continue;
            var separator = Document.GetLineByNumber(table.SeparatorLine + 1);
            _collapsedQuotes.Add(TextArea.TextView.CollapseLines(separator, separator));
        }
    }

    /// <summary>展开 / 收起引用面板。</summary>
    internal void ToggleQuote(QuoteBlock quote)
    {
        SetQuoteExpanded(quote, !IsQuoteExpanded(quote));
        _animateQuote = quote.Start;
        TextArea.TextView.Redraw();
    }

    /// <summary>刚展开 / 收起的引用：面板重建时播放一次动画（其它原因的重绘不播放）。</summary>
    internal bool TakeQuoteAnimation(QuoteBlock quote)
    {
        if (_animateQuote != quote.Start)
            return false;
        _animateQuote = -1;
        return true;
    }

    /// <summary>删除整段引用（连同后面的换行，引用在最后时连同前面的换行），一步撤销。</summary>
    internal void DeleteQuote(QuoteBlock quote)
    {
        if (quote.End > Document.TextLength)
            return;
        var start = quote.Start;
        var end = quote.End;
        if (end < Document.TextLength && Document.GetCharAt(end) == '\n')
            end++;
        else if (start > 0 && Document.GetCharAt(start - 1) == '\n')
            start--;
        SetQuoteExpanded(quote, false);
        if (_editQuote?.Offset == quote.Start)
            _editQuote = null;
        Document.Remove(start, end - start);
        CaretIndex = start;
    }

    private void SetQuoteExpanded(QuoteBlock quote, bool expanded)
    {
        _expandedQuotes.RemoveAll(a => a.IsDeleted || a.Offset == quote.Start);
        if (!expanded)
            return;
        var anchor = Document.CreateAnchor(quote.Start);
        // 在引用前面输入时锚点跟着引用移动
        anchor.MovementType = AnchorMovementType.AfterInsertion;
        _expandedQuotes.Add(anchor);
    }

    /// <summary>编辑中的引用点击标题：结束编辑并收起（光标移到引用之后）。</summary>
    internal void CollapseQuote(QuoteBlock quote)
    {
        SetQuoteExpanded(quote, false);
        _editQuote = null;
        _animateQuote = quote.Start;
        CaretIndex = quote.End;
        TextArea.TextView.Redraw();
    }

    /// <summary>编辑引用：展开，光标移到被引用的内容开头，在面板中显示原文。</summary>
    internal void EditQuote(QuoteBlock quote)
    {
        SetQuoteExpanded(quote, true);
        _editQuote = Document.CreateAnchor(quote.Start);
        _editQuote.MovementType = AnchorMovementType.AfterInsertion;
        TextArea.Focus();
        CaretIndex = quote.BodyStart;
        TextArea.Caret.BringCaretToView();
    }

    internal void JumpToQuote(QuoteBlock quote)
    {
        if (quote.MessageId is { } id)
            QuoteJumpRequested?.Invoke(this, id);
    }

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
        int quote;
        if (TextArea.IsKeyboardFocusWithin)
        {
            var caret = CaretOffset;
            line = Model.LineIndexAt(caret);
            if (line >= 0 && !RevealsLine(Model.Lines[line]))
                line = -1;
            spans = Model.Spans.Where(s => Reveals(s.Kind) && s.Start <= caret && caret <= s.End).ToList();
            var inside = Quotes.FirstOrDefault(q => q.Start < caret && caret < q.End);
            if (inside is null)
            {
                _editQuote = null;
            }
            else if (!IsSourceMode && (_editQuote is null || _editQuote.IsDeleted || _editQuote.Offset != inside.Start))
            {
                // 只有点编辑进入的引用可以编辑：光标以其它方式（方向键、撤销等）进入引用时移到较近的一端；选择时只保持面板
                if (TextArea.Selection.IsEmpty)
                {
                    CaretOffset = caret - inside.Start <= inside.End - caret ? inside.Start : inside.End;
                    return;
                }
                inside = null;
            }
            quote = inside?.Start ?? -1;
        }
        else
        {
            line = -1;
            spans = [];
            quote = -1;
        }
        var activeTable = ActiveTableLine();
        if (line == _revealLine && spans.SequenceEqual(_revealSpans) && quote == _revealQuote && activeTable == _activeTableLine)
        {
            // 同一个表格中换了一列：对齐按钮要更新
            // 基类构造时就会触发 OnTextChanged，此时工具栏层还没有创建
            _tableToolbarLayer?.Update();
            return;
        }
        _revealLine = line;
        _revealSpans = spans;
        _revealQuote = quote;
        _activeTableLine = activeTable;
        UpdateQuoteCollapse();
        TextArea.TextView.Redraw();
    }

    // 有焦点（或工具栏的菜单开着）、可以编辑时光标所在的表格；按原文显示的表格没有工具栏
    private int ActiveTableLine()
    {
        if (IsReadOnly || !TextArea.IsKeyboardFocusWithin && !_tablePopupOpen)
            return -1;
        var index = Model.LineIndexAt(CaretOffset);
        return index >= 0 && TableAt(index) is { } found ? found.Table.HeaderLine : -1;
    }

    /// <summary>编辑中的表格：标题行上方留出工具栏的位置。</summary>
    internal bool IsTableActive(MarkdownTable table) => table.HeaderLine == _activeTableLine;

    // ───────────── 输入 ─────────────

    // 换行由 TextArea 换成文档的换行符（文档只有一行时为系统换行符 \r\n），这里直接插入 \n
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is "\n" or "\r" or "\r\n")
        {
            e.Handled = true;
            // 表格的一行不能换行：跳到表格下面的新行
            if (ActiveTable is { } table)
            {
                var end = Model.Lines[table.Table.LastLine].End;
                TextArea.ClearSelection();
                Document.Insert(end, "\n");
                CaretIndex = end + 1;
                TextArea.Caret.BringCaretToView();
                return;
            }
            TextArea.Selection.ReplaceSelectionWithText("\n");
            TextArea.Caret.BringCaretToView();
        }
        else if (!string.IsNullOrEmpty(e.Text) && InsertBesideQuote(e.Text))
        {
            e.Handled = true;
        }
    }

    /// <summary>
    /// 光标紧挨着显示为面板的引用（在它的开头或结尾）时插入 <paramref name="text"/>：另起一行，
    /// 否则文字会接在引用行前面或结束的围栏后面，引用不再完整。没有紧挨着时返回 false，不插入。
    /// </summary>
    private bool InsertBesideQuote(string text)
    {
        var caret = CaretOffset;
        if (IsSourceMode || !TextArea.Selection.IsEmpty
            || Quotes.FirstOrDefault(q => !IsQuoteRevealed(q) && (q.Start == caret || q.End == caret)) is not { } quote)
            return false;
        if (caret == quote.End)
        {
            Document.Insert(caret, "\n" + text);
            CaretIndex = caret + 1 + text.Length;
        }
        else
        {
            Document.Insert(caret, text + "\n");
            CaretIndex = caret + text.Length;
        }
        TextArea.Caret.BringCaretToView();
        return true;
    }

    // 单击提及、频道链接、表情、图片等整体元素：松开后选中整个元素
    private void OnPointerPressedTunnel(object? sender, PointerPressedEventArgs e)
    {
        _pressedAtom = null;
        var point = e.GetCurrentPoint(TextArea.TextView);
        if (!point.Properties.IsLeftButtonPressed || e.ClickCount != 1 || e.KeyModifiers.HasFlag(KeyModifiers.Shift))
            return;
        // 点在引用面板（或编辑中的引用的标题栏）上：由面板处理
        if (e.Source is Visual source && source.FindAncestorOfType<QuotePanel>(includeSelf: true) is not null)
            return;
        _pressedAtom = AtomAt(point.Position);
    }

    private void OnPointerReleasedAny(object? sender, PointerReleasedEventArgs e)
    {
        PlaceCaretInClickedCell(e);
        if (_pressedAtom is not { } atom)
            return;
        _pressedAtom = null;
        if (e.InitialPressMouseButton != MouseButton.Left || atom.End > Document.TextLength)
            return;
        if (AtomAt(e.GetPosition(TextArea.TextView)) == atom)
            Select(atom.Start, atom.End - atom.Start);
    }

    // 点在表格单元格的空白处：| 显示成的空白属于两格之间，光标可能落到下一格，这里放回点到的那一格
    private void PlaceCaretInClickedCell(PointerReleasedEventArgs e)
    {
        var textView = TextArea.TextView;
        if (IsSourceMode || e.InitialPressMouseButton != MouseButton.Left || !TextArea.Selection.IsEmpty
            || (e.Source as Visual)?.FindAncestorOfType<MarkdownTableToolbar>(includeSelf: true) is not null)
            return;
        var point = e.GetPosition(textView) + textView.ScrollOffset;
        if (textView.GetPositionFloor(point) is not { } position)
            return;
        var index = position.Line - 1;
        if (TableAt(index) is not { } found)
            return;
        var column = found.Layout.ColumnAt(point.X);
        if (MarkdownTableEdit.CellAt(Model, Document.Text, found.Table, CaretOffset) is var cell
            && Model.LineIndexAt(CaretOffset) == index && cell.Column == column)
            return;
        if (found.Layout.CaretInCell(index, column) is { } caret)
            CaretOffset = caret;
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
