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
    // 文档中每个表格显示成的控件（与 Tables 一一对应）
    private readonly List<MarkdownTablePanel> _tablePanels = [];
    // Model 对应的文档版本：文档修改后、重新解析前（此时光标已经移动）Model 是旧的
    private ITextSourceVersion? _modelVersion;
    // 光标所在的单元格
    private MarkdownTablePanel? _activePanel;
    private MarkdownEditor? _activeCell;
    private int _activeRow;
    private int _activeColumn;
    // 工具栏的菜单打开时焦点不在单元格中，工具栏仍然显示
    private bool _tablePopupOpen;
    // 正在修改表格（光标在表格的范围内也不用进入单元格，修改完会处理）
    private bool _editingTable;
    private int _lastCaret;
    // 这个编辑器是单元格时：自动换行的位置（键为文档行）
    private Dictionary<int, IReadOnlyList<SoftBreak>> _softBreaks = [];
    private object? _softBreaksVersion;
    private MdSpan? _pressedAtom;

    /// <summary>这个编辑器是表格的一个单元格（其中的文字不再解析出表格）。</summary>
    internal bool IsTableCell { get; init; }

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
        // 引用面板的宽度跟随输入框（第一次生成时输入框可能还没有宽度）；表格的列宽也取决于宽度
        textView.SizeChanged += (_, e) =>
        {
            if (e.WidthChanged && (Quotes.Count > 0 || Tables.Count > 0))
                Dispatcher.UIThread.Post(() =>
                {
                    UpdateQuoteCollapse();
                    textView.Redraw();
                });
        };
        _preeditLayer = new PreeditLayer(this);
        textView.InsertLayer(_preeditLayer, KnownLayer.Caret, LayerInsertionPosition.Above);
        _ime = new MarkdownImeClient(this);

        TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        TextArea.SelectionChanged += (_, _) => UpdateTableSelection();
        TextArea.GotFocus += (_, e) =>
        {
            // 焦点事件会从单元格冒泡上来
            if (IsInTablePanel(e.Source))
                return;
            ClearActiveCell();
            _ime.Attach(TextArea);
            RefreshReveal();
        };
        TextArea.LostFocus += (_, e) =>
        {
            if (IsInTablePanel(e.Source))
                return;
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
        ActualThemeVariantChanged += (_, _) =>
        {
            // 表格的颜色是创建时取的：重新创建
            ClearActiveCell();
            _tablePanels.Clear();
            UpdateQuoteCollapse();
            TextArea.TextView.Redraw();
        };
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
        Tables = IsTableCell ? [] : MarkdownTables.Find(Model);
        _modelVersion = Document.Version;
    }

    /// <summary>光标所在的单元格（失去焦点后仍然记得，例如打开了工具栏的菜单）；不在表格中为 null。</summary>
    public MarkdownEditor? ActiveCell => _activeCell;

    /// <summary>光标所在的编辑器：单元格，或者这个输入框。格式、粘贴、补全都作用于它。</summary>
    public MarkdownEditor FocusedEditor => _activeCell ?? this;

    /// <summary>光标所在单元格的表格。</summary>
    internal MarkdownTable? ActiveTable => _activePanel?.Table;

    internal bool IsActivePanel(MarkdownTablePanel panel) =>
        _activePanel == panel && (panel.IsKeyboardFocusWithin || _tablePopupOpen);

    internal (int Row, int Column)? ActiveCellPosition(MarkdownTablePanel panel) =>
        _activePanel == panel ? (_activeRow, _activeColumn) : null;

    internal void SetActiveCell(MarkdownTablePanel panel, int row, int column, MarkdownEditor cell)
    {
        var other = _activePanel != panel ? _activePanel : null;
        (_activePanel, _activeRow, _activeColumn, _activeCell) = (panel, row, column, cell);
        // 外层的光标不显示（焦点在它的子控件中时它也认为自己有焦点）
        TextArea.Caret.CaretBrush = Brushes.Transparent;
        _ime.Attach(null);
        other?.UpdateToolbar();
        panel.UpdateToolbar();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    internal void RefocusActiveCell()
    {
        if (_activeCell is { IsKeyboardFocusWithin: false } cell && !_tablePopupOpen && _activePanel is { IsKeyboardFocusWithin: true })
            cell.TextArea.Focus();
    }

    internal void OnCellCaretMoved()
    {
        _activePanel?.UpdateToolbar();
        CaretMoved?.Invoke(this, EventArgs.Empty);
    }

    private void ClearActiveCell()
    {
        if (_activeCell is null)
            return;
        var panel = _activePanel;
        (_activePanel, _activeCell) = (null, null);
        TextArea.Caret.CaretBrush = null;
        panel?.UpdateToolbar();
    }

    /// <summary>单元格的文字变了：写回文档（连续输入合并为一步撤销）。</summary>
    internal void WriteCell(MarkdownTablePanel panel, int row, int column, string value)
    {
        var table = panel.Table;
        if (!ReferenceEquals(Document.Version, _modelVersion) || IsReadOnly)
            return;
        var edit = MarkdownTableEdit.SetCell(Model, Document.Text, table, row, column, value);
        _editingTable = true;
        try
        {
            // 同一个单元格中连续的修改合并
            var cell = panel.Cell(row, column);
            if (cell is not null && ReferenceEquals(Document.UndoStack.LastGroupDescriptor, cell))
                Document.UndoStack.StartContinuedUndoGroup(cell);
            else
                Document.UndoStack.StartUndoGroup(cell);
            Document.Replace(edit.Start, edit.Length, edit.Replacement);
            Document.UndoStack.EndUndoGroup();
        }
        finally
        {
            _editingTable = false;
        }
    }

    /// <summary>当前列的对齐方式。</summary>
    internal TableAlign ActiveTableAlign
    {
        get
        {
            if (ActiveTable is not { } table)
                return TableAlign.Left;
            var aligns = MarkdownTableEdit.Aligns(Model, Document.Text, table);
            return _activeColumn < aligns.Count ? aligns[_activeColumn] : TableAlign.Left;
        }
    }

    /// <summary>当前表格的列数与行数（含标题行）。</summary>
    internal (int Columns, int Rows) ActiveTableSize =>
        ActiveTable is { } table
            ? (MarkdownTableEdit.ColumnCount(Model, Document.Text, table), MarkdownTableEdit.RowCount(table))
            : (MarkdownTableEdit.MinColumns, MarkdownTableEdit.MinRows);

    /// <summary>修改当前表格（一步撤销）；操作不了（返回 null）时不改。之后焦点回到目标单元格。</summary>
    internal void EditActiveTable(Func<MarkdownModel, string, MarkdownTable, int, MarkdownEdit?> edit)
    {
        if (ActiveTable is not { } table)
            return;
        var text = Document.Text;
        var caret = MarkdownTableEdit.CaretOf(Model, text, table, _activeRow, _activeColumn);
        if (!IsReadOnly && edit(Model, text, table, caret) is { } change)
        {
            _editingTable = true;
            try
            {
                ApplyEdit(change);
            }
            finally
            {
                _editingTable = false;
            }
            var at = CaretOffset;
            var index = Tables.ToList().FindIndex(t => Model.Lines[t.HeaderLine].Start <= at && at <= Model.Lines[t.LastLine].End);
            if (index >= 0)
            {
                var (row, column) = MarkdownTableEdit.CellAt(Model, Document.Text, Tables[index], at);
                var panel = _tablePanels[index];
                (_activePanel, _activeRow, _activeColumn, _activeCell) = (panel, row, column, panel.Cell(row, column));
                Dispatcher.UIThread.Post(() => panel.FocusCell(row, column));
                return;
            }
            // 表格删除了
            ClearActiveCell();
            TextArea.Focus();
            return;
        }
        _activeCell?.TextArea.Focus();
    }

    /// <summary>复制当前表格的原文。</summary>
    internal async Task CopyActiveTableAsync()
    {
        if (ActiveTable is not { } table || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        var source = MarkdownTableEdit.Source(Model, Document.Text, table);
        await MarkdownClipboard.SetAsync(clipboard, source, source);
        _activeCell?.TextArea.Focus();
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
            _activeCell?.TextArea.Focus();
            _activePanel?.UpdateToolbar();
        });
    }

    /// <summary>插入空表格，光标放在第一个单元格。</summary>
    public void InsertTable(int columns, int rows) =>
        ApplyEdit(MarkdownFormat.InsertTable(Text, SelectionStart, SelectionEnd, columns, rows));

    /// <summary>
    /// 在当前表格的上方或下方插入一个空段落，光标移到那里。表格占满了宽度，表格紧挨文字或在文档两端时不方便把光标放到外面。
    /// </summary>
    public void InsertParagraphBesideTable(bool above)
    {
        if (ActiveTable is not { } table)
            return;
        var position = above ? Model.Lines[table.HeaderLine].Start : Model.Lines[table.LastLine].End;
        ClearActiveCell();
        _editingTable = true;
        try
        {
            Document.Insert(position, "\n\n");
            CaretIndex = above ? position : position + 2;
        }
        finally
        {
            _editingTable = false;
        }
        TextArea.Focus();
    }

    /// <summary>离开当前表格：到上一行的末尾或下一行的开头。那里没有行时：<paramref name="createLine"/> 为 true 则新建一行，否则不动。</summary>
    internal void LeaveTable(bool above, bool createLine)
    {
        if (ActiveTable is not { } table)
            return;
        int target;
        var hasLine = above ? table.HeaderLine > 0 : table.LastLine + 1 < Model.Lines.Count;
        if (hasLine)
        {
            target = above ? Model.Lines[table.HeaderLine - 1].End : Model.Lines[table.LastLine + 1].Start;
        }
        else if (createLine && !above)
        {
            target = Model.Lines[table.LastLine].End + 1;
            _editingTable = true;
            Document.Insert(target - 1, "\n");
            _editingTable = false;
        }
        else
        {
            return;
        }
        ClearActiveCell();
        _editingTable = true;
        CaretIndex = target;
        _editingTable = false;
        TextArea.Focus();
        TextArea.Caret.BringCaretToView();
    }

    /// <summary>表格的快捷键（光标在单元格中时）；处理了返回 true。</summary>
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

    /// <summary>
    /// 光标与表格有关的按键（在 @ 补全列表之后处理：列表打开时上下键选择列表项）。
    /// 单元格中见 <see cref="MarkdownTablePanel.HandleKey"/>；表格外紧挨表格的退格和 Delete 不能把文字并进表格。处理了返回 true。
    /// </summary>
    public bool HandleTableCaretKey(KeyEventArgs e)
    {
        if (_activeCell is { HasPreedit: true } || HasPreedit)
            return false;
        if (HandleWrappedSpaceRight(e))
            return true;
        if (_activePanel is { } panel && _activeCell is { IsKeyboardFocusWithin: true })
            return panel.HandleKey(e, _activeRow, _activeColumn);
        if (e.Key is not (Key.Back or Key.Delete) || SelectionLength > 0 || IsSourceMode)
            return false;
        var caret = CaretOffset;
        foreach (var table in Tables)
        {
            var besides = e.Key == Key.Back
                ? caret == Model.Lines[table.LastLine].End + 1 && table.LastLine + 1 < Model.Lines.Count && Model.Lines[table.LastLine + 1].End > caret
                : caret + 1 == Model.Lines[table.HeaderLine].Start && table.HeaderLine > 0 && Model.Lines[table.HeaderLine - 1].Start < caret;
            if (!besides)
                continue;
            e.Handled = true;
            return true;
        }
        return false;
    }

    // 表格显示为控件（不重建，见 MarkdownTablePanel）。在文字解析和宽度变化后调用
    private void UpdateTablePanels()
    {
        var tables = IsSourceMode || IsTableCell ? [] : Tables;
        if (_activePanel is not null && _tablePanels.IndexOf(_activePanel) >= tables.Count)
        {
            // 光标所在的表格没有了（撤销、发送、切换到源码模式）
            ClearActiveCell();
            Dispatcher.UIThread.Post(() =>
            {
                if (IsKeyboardFocusWithin || TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is null)
                    TextArea.Focus();
            });
        }
        if (_tablePanels.Count > tables.Count)
            _tablePanels.RemoveRange(tables.Count, _tablePanels.Count - tables.Count);
        var text = Document.Text;
        var width = Math.Max(120, TextArea.TextView.Bounds.Width - 2);
        for (var i = 0; i < tables.Count; i++)
        {
            if (i == _tablePanels.Count)
                _tablePanels.Add(new MarkdownTablePanel(this, tables[i]));
            _tablePanels[i].Update(tables[i], MarkdownTableEdit.CellValues(Model, text, tables[i]),
                MarkdownTableEdit.Aligns(Model, text, tables[i]), width);
        }
    }

    /// <summary>这个表格显示成的控件；源码模式下为 null。</summary>
    internal MarkdownTablePanel? PanelOf(MarkdownTable table)
    {
        var index = Tables is List<MarkdownTable> list ? list.IndexOf(table) : Tables.ToList().IndexOf(table);
        return index >= 0 && index < _tablePanels.Count ? _tablePanels[index] : null;
    }

    // 一段单元格文字显示的宽度
    private double MeasureCellText(string text) =>
        text.Length == 0 ? 0 : new FormattedText(PlainText(text), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily, FontStyle.Normal, FontWeight), FontSize, null).WidthIncludingTrailingWhitespace;

    // 这一行（文档行）可以断行的位置：代码、链接、提及等的内部不断开
    private List<SoftBreak> SoftBreakCandidates(int index)
    {
        var line = Model.Lines[index];
        var spans = Model.Spans.Where(s => s.Kind is not (MdSpanKind.Strong or MdSpanKind.Em or MdSpanKind.StrongEm or MdSpanKind.Del)
                                           && s.End > line.Start && s.Start < line.End).ToList();
        return MarkdownTableWrap.Candidates(Document.GetText(line.Start, line.End - line.Start),
            i => spans.Exists(s => s.Start <= line.Start + i && line.Start + i < s.End));
    }

    /// <summary>单元格（的文字）最少需要的宽度（最长的不能断开的一段）和不换行时的宽度。</summary>
    internal (double Minimum, double Required) MeasureCellWrap()
    {
        double minimum = 0, required = 0;
        for (var index = 0; index < Model.Lines.Count; index++)
        {
            var line = Model.Lines[index];
            var text = Document.GetText(line.Start, line.End - line.Start);
            minimum = Math.Max(minimum, MarkdownTableWrap.Widest(text, SoftBreakCandidates(index), MeasureCellText));
            required = Math.Max(required, MeasureCellText(text));
        }
        return (minimum, required);
    }

    /// <summary>按 <paramref name="width"/> 决定单元格在哪里自动换行，返回最宽的一行的宽度。</summary>
    internal double ApplyCellWrap(double width)
    {
        var breaks = new Dictionary<int, IReadOnlyList<SoftBreak>>();
        var widest = 0.0;
        for (var index = 0; index < Model.Lines.Count; index++)
        {
            var line = Model.Lines[index];
            var text = Document.GetText(line.Start, line.End - line.Start);
            var chosen = MarkdownTableWrap.Choose(text, SoftBreakCandidates(index), MeasureCellText, width);
            if (chosen.Count > 0)
                breaks[index] = chosen;
            widest = Math.Max(widest, MarkdownTableWrap.Widest(text, chosen, MeasureCellText));
        }
        // 文字改过时也重画：改完到这里之间生成的可视行没有断行
        var changed = !ReferenceEquals(Document.Version, _softBreaksVersion)
                      || breaks.Count != _softBreaks.Count
                      || breaks.Any(b => !_softBreaks.TryGetValue(b.Key, out var old) || !old.SequenceEqual(b.Value));
        _softBreaks = breaks;
        _softBreaksVersion = Document.Version;
        if (changed)
            TextArea.TextView.Redraw();
        return widest;
    }

    /// <summary>这一行（文档行）自动换行的位置（行内偏移）。文档改过、还没有重新计算时为空。</summary>
    internal IReadOnlyList<SoftBreak> SoftBreaksOn(int index) =>
        ReferenceEquals(Document.Version, _softBreaksVersion) && _softBreaks.TryGetValue(index, out var breaks) ? breaks : [];

    /// <summary>光标在单元格显示的第一行或最后一行（算上自动换行）：再按上下键就离开这个单元格。</summary>
    internal bool IsCaretOnEdgeLine(bool first)
    {
        var caret = TextArea.Caret;
        if (caret.Line != (first ? 1 : Document.LineCount))
            return false;
        if (TextArea.TextView.GetVisualLine(caret.Line) is not { } line)
            return true;
        var textLine = line.GetTextLine(caret.VisualColumn, caret.Position.IsAtEndOfLine);
        return ReferenceEquals(textLine, first ? line.TextLines[0] : line.TextLines[^1]);
    }

    // 代码模式下表格的一行被换行时，第一行末尾的空格之前按右方向键：到空格之后，仍然在第一行（默认会到第二行的开头）
    private bool HandleWrappedSpaceRight(KeyEventArgs e)
    {
        var caret = TextArea.Caret;
        if (!IsSourceMode || e.Key != Key.Right || e.KeyModifiers != KeyModifiers.None || SelectionLength > 0
            || caret.Offset >= Document.TextLength || Document.GetCharAt(caret.Offset) is not (' ' or '　')
            || !Tables.Any(t => t.HeaderLine <= caret.Line - 1 && caret.Line - 1 <= t.LastLine)
            || TextArea.TextView.GetVisualLine(caret.Line) is not { } line)
            return false;
        var column = caret.VisualColumn;
        var textLine = line.GetTextLine(column, caret.Position.IsAtEndOfLine);
        var index = line.TextLines.ToList().IndexOf(textLine);
        var end = line.TextLines.Take(index + 1).Sum(l => l.Length);
        if (index + 1 >= line.TextLines.Count || column + 1 != end)
            return false;
        e.Handled = true;
        caret.Position = new TextViewPosition(caret.Line, caret.Column + 1, column + 1) { IsAtEndOfLine = true };
        return true;
    }

    // 光标落到了表格上（方向键、点在表格旁边、把光标放到文档末尾等）：表格是一个元素，光标要进入单元格
    private void EnterTableAtCaret()
    {
        var previous = _lastCaret;
        var caret = CaretOffset;
        _lastCaret = caret;
        if (IsSourceMode || IsTableCell || _editingTable || !TextArea.IsFocused
            || !ReferenceEquals(Document.Version, _modelVersion) || !TextArea.Selection.IsEmpty)
            return;
        for (var i = 0; i < Tables.Count && i < _tablePanels.Count; i++)
        {
            var table = Tables[i];
            var (start, end) = (Model.Lines[table.HeaderLine].Start, Model.Lines[table.LastLine].End);
            if (caret < start || caret > end)
                continue;
            var panel = _tablePanels[i];
            var (row, column) = caret > start && caret < end ? MarkdownTableEdit.CellAt(Model, Document.Text, table, caret)
                : caret == end ? (MarkdownTableEdit.RowCount(table) - 1, int.MaxValue)
                : previous > end ? (MarkdownTableEdit.RowCount(table) - 1, 0) : (0, 0);
            // 光标没有动，只是焦点回到了输入框（切换窗口、关闭菜单）：回到原来的单元格，光标和滚动位置都不变
            var focusReturned = previous == caret;
            Dispatcher.UIThread.Post(() =>
            {
                // 等待期间全选了：表格作为整体被选中，不进入单元格。
                // 点表格外时先得到焦点、然后光标才移走：此时已经离开了表格
                if (TextArea.Selection.IsEmpty && CaretOffset == caret && TextArea.IsFocused && !(focusReturned && panel.RestoreFocus()))
                    panel.FocusCell(row, column);
            });
            return;
        }
    }

    // 外层全选时表格的文字变成白色
    private void UpdateTableSelection()
    {
        for (var i = 0; i < Tables.Count && i < _tablePanels.Count; i++)
        {
            var selected = SelectionLength > 0 && SelectionStart <= Model.Lines[Tables[i].HeaderLine].Start
                           && SelectionEnd >= Model.Lines[Tables[i].LastLine].End;
            _tablePanels[i].SetSelected(selected);
        }
    }

    private bool IsInTablePanel(object? source) =>
        !IsTableCell && (source as Visual)?.FindAncestorOfType<MarkdownTablePanel>(includeSelf: true) is not null;

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
        UpdateTablePanels();
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
        // 表格显示为一个控件（在 MarkdownElementGenerator 中），第一行以外的行折叠
        foreach (var table in Tables)
        {
            if (table.LastLine + 1 > Document.LineCount)
                continue;
            _collapsedQuotes.Add(TextArea.TextView.CollapseLines(
                Document.GetLineByNumber(table.HeaderLine + 2), Document.GetLineByNumber(table.LastLine + 1)));
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
        EnterTableAtCaret();
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
        if (line == _revealLine && spans.SequenceEqual(_revealSpans) && quote == _revealQuote)
            return;
        _revealLine = line;
        _revealSpans = spans;
        _revealQuote = quote;
        UpdateQuoteCollapse();
        TextArea.TextView.Redraw();
    }

    // ───────────── 输入 ─────────────

    // 换行由 TextArea 换成文档的换行符（文档只有一行时为系统换行符 \r\n），这里直接插入 \n
    private void OnTextEntering(object? sender, TextInputEventArgs e)
    {
        if (e.Text is "\n" or "\r" or "\r\n")
        {
            e.Handled = true;
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
        if (e.Source is Visual source && (source.FindAncestorOfType<QuotePanel>(includeSelf: true) is not null || IsInTablePanel(source)))
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
        // 请求从单元格冒泡上来时，外层 TextArea 的类处理器已经把它换成了外层的客户端
        if (IsInTablePanel(e.Source) && _activeCell is { } cell)
            e.Client = cell._ime;
        else if (!IsReadOnly && IsEnabled)
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
