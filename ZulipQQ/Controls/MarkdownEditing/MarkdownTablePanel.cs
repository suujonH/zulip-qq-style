using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 输入框中的表格：整个表格的原文在外层编辑器中是一个元素，显示为这个控件。上面是工具栏（位置始终留着，出现时表格不移动），
/// 中间是单元格（每个单元格一个 <see cref="MarkdownEditor"/>，可以真的换行），下面是常驻的水平滚动条（不盖住表格）。
/// 外层文字变化时控件不重建（<see cref="Update"/>），否则输入时单元格会失去焦点。
/// </summary>
internal sealed class MarkdownTablePanel : Border
{
    private const double CellPaddingX = 8;
    private const double CellPaddingY = 4;
    private const double CaretRoom = 4;
    private const double MinTextWidth = 16;

    private readonly MarkdownEditor _owner;
    private readonly MarkdownTableToolbar _toolbar;
    private readonly Grid _grid = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly ScrollViewer _scroll;
    private readonly List<List<MarkdownEditor>> _cells = [];
    private IReadOnlyList<TableAlign> _aligns = [];
    private double _available;
    private bool _syncing;
    private const double DragThreshold = 4;
    // 鼠标按下、还没有拖动：单纯的点击不滚动表格
    private bool _pointerDown;
    private Point _pressPoint;
    private MarkdownEditor? _lastCell;

    public MarkdownTablePanel(MarkdownEditor owner, MarkdownTable table)
    {
        _owner = owner;
        Table = table;
        // 右边的按钮可能移到表格上面（PinActions）：工具栏要在表格之上
        _toolbar = new MarkdownTableToolbar(owner) { Opacity = 0, IsHitTestVisible = false, ZIndex = 1 };
        var host = new HorizontalScrollHost(_grid, reserveBar: true);
        _scroll = host.Viewer;
        Child = new StackPanel { Children = { _toolbar, host } };
        // 点工具栏、滚动条这些不接收焦点的控件时，焦点落到最近的可以接收焦点的上层：是这里而不是外层编辑器（否则光标就离开了表格）。
        // 松开后还给单元格
        Focusable = true;
        IsTabStop = false;
        AddHandler(PointerReleasedEvent, (_, _) =>
        {
            _pointerDown = false;
            Dispatcher.UIThread.Post(_owner.RefocusActiveCell);
        }, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            _pointerDown = true;
            _pressPoint = e.GetPosition(this);
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 拖动起来就是在选择文字：滚动条跟随光标。手抖的几个像素仍算点击
        AddHandler(PointerMovedEvent, (_, e) =>
        {
            if (_pointerDown && Math.Abs(e.GetPosition(this).X - _pressPoint.X) > DragThreshold)
                _pointerDown = false;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        // 单元格获得焦点时会请求滚动到可见，比可视区宽的单元格因此对齐到左边，点击时表格就跳动了。
        // 横向由 EnsureCaretVisible 负责；纵向仍交给外层（表格比输入框高时）
        _grid.AddHandler(RequestBringIntoViewEvent, (_, e) =>
        {
            e.Handled = true;
            if (e.TargetObject is { } target && target.TranslatePoint(e.TargetRect.TopLeft, this) is { } top)
                this.BringIntoView(new Rect(0, top.Y, Bounds.Width, e.TargetRect.Height));
        });
        // 表格的内部：点击由单元格或这里处理，不移动外层的光标
        PointerPressed += (_, e) => e.Handled = true;
        AddHandler(GotFocusEvent, (_, e) =>
        {
            // 不让事件冒泡到外层：TextEditor 收到后会把焦点转给自己的 TextArea
            e.Handled = true;
            UpdateToolbar();
        }, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, (_, _) => Dispatcher.UIThread.Post(UpdateToolbar), RoutingStrategies.Bubble);
    }

    public MarkdownTable Table { get; private set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // 输入框滚动时表格重新排列，Bounds.Y 就是表格上边相对可视区的位置（TextView 的坐标不随滚动变化）
        if (change.Property == BoundsProperty)
            _toolbar.PinActions(Math.Clamp(-Bounds.Y, 0, Math.Max(0, Bounds.Height - _toolbar.Height)));
    }

    /// <summary>外层文字或宽度变化后更新；单元格的数量没变时保留原来的单元格（和焦点）。</summary>
    public void Update(MarkdownTable table, IReadOnlyList<IReadOnlyList<string>> values, IReadOnlyList<TableAlign> aligns, double available)
    {
        Table = table;
        _aligns = aligns;
        _available = available;
        Width = available;
        if (_cells.Count != values.Count || _cells.Count == 0 || _cells[0].Count != values[0].Count)
            Rebuild(values);
        _syncing = true;
        try
        {
            for (var r = 0; r < values.Count; r++)
            {
                for (var c = 0; c < values[r].Count; c++)
                {
                    // 正在输入时单元格两端的空格在原文中被当作排版空格：比较时不算，否则刚输入的空格会被去掉
                    if (_cells[r][c].Text.Trim() != values[r][c])
                        _cells[r][c].Markdown = values[r][c];
                }
            }
        }
        finally
        {
            _syncing = false;
        }
        UpdateLayout(values);
        UpdateToolbar();
    }

    /// <summary>把焦点放到单元格，光标在文字的开头或末尾。</summary>
    public void FocusCell(int row, int column, bool atEnd = true)
    {
        if (_cells.Count == 0)
            return;
        var cells = _cells[Math.Clamp(row, 0, _cells.Count - 1)];
        var cell = cells[Math.Clamp(column, 0, cells.Count - 1)];
        cell.CaretIndex = atEnd ? cell.Text.Length : 0;
        cell.TextArea.Focus();
        if (!_pointerDown)
            EnsureCaretVisible(cell);
    }

    /// <summary>焦点回到上次所在的单元格，光标和滚动位置都不变。没有这样的单元格时返回 false。</summary>
    public bool RestoreFocus()
    {
        if (_lastCell is not { Parent: not null } cell)
            return false;
        cell.TextArea.Focus();
        return true;
    }

    /// <summary>外层选中了整个表格：文字变成白色（选择的背景是主题色）。</summary>
    public void SetSelected(bool selected)
    {
        foreach (var cell in _cells.SelectMany(row => row))
            cell.Foreground = selected ? Brushes.White : _owner.Foreground;
    }

    public void UpdateToolbar()
    {
        var active = _owner.IsActivePanel(this);
        _toolbar.Opacity = active ? 1 : 0;
        _toolbar.IsHitTestVisible = active;
        if (active)
            _toolbar.Refresh();
    }

    public MarkdownEditor? Cell(int row, int column) =>
        row < _cells.Count && column < _cells[row].Count ? _cells[row][column] : null;

    private void Rebuild(IReadOnlyList<IReadOnlyList<string>> values)
    {
        var refocus = _owner.ActiveCellPosition(this);
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        _grid.ColumnDefinitions.Clear();
        _cells.Clear();
        var columns = values.Count == 0 ? 0 : values[0].Count;
        for (var c = 0; c < columns; c++)
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        for (var r = 0; r < values.Count; r++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var row = new List<MarkdownEditor>();
            for (var c = 0; c < columns; c++)
            {
                var cell = CreateCell(r, c);
                row.Add(cell);
                var host = new Border
                {
                    Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Ibeam),
                    Padding = new Thickness(CellPaddingX, CellPaddingY),
                    BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                    BorderBrush = _owner.Brush("TableBorderBrush"),
                    Child = cell,
                };
                var (hostRow, hostColumn) = (r, c);
                // 点在单元格的空白处
                host.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    FocusCell(hostRow, hostColumn);
                };
                Grid.SetRow(host, r);
                Grid.SetColumn(host, c);
                _grid.Children.Add(host);
            }
            _cells.Add(row);
        }
        if (refocus is { } position)
            Dispatcher.UIThread.Post(() => FocusCell(position.Row, position.Column));
    }

    private MarkdownEditor CreateCell(int row, int column)
    {
        var cell = new MarkdownEditor
        {
            IsTableCell = true,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            WordWrap = false,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = _owner.FontFamily,
            FontSize = _owner.FontSize,
            FontWeight = row == 0 ? FontWeight.Bold : FontWeight.Normal,
            Foreground = _owner.Foreground,
            IsReadOnly = _owner.IsReadOnly,
            EmojiLookup = _owner.EmojiLookup,
        };
        cell.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        cell.Resolver = _owner.Resolver;
        // 撤销由外层统一处理（单元格的修改都写回外层的文档）
        cell.Document.UndoStack.SizeLimit = 0;
        cell.TextChanged += (_, _) =>
        {
            if (!_syncing)
                _owner.WriteCell(this, row, column, cell.Text);
        };
        cell.CaretMoved += (_, _) =>
        {
            // 鼠标点的地方本来就在眼前：点在长列的空白处时光标可能在可视区外，也不滚动，等输入时再滚动
            // 没有焦点的单元格：是外层写入文字带动的光标（如加载草稿），不能把表格滚到那里
            if (!_pointerDown && cell.TextArea.IsFocused)
                EnsureCaretVisible(cell);
            _owner.OnCellCaretMoved();
        };
        cell.TextArea.GotFocus += (_, _) =>
        {
            _lastCell = cell;
            _owner.SetActiveCell(this, row, column, cell);
        };
        return cell;
    }

    private void UpdateLayout(IReadOnlyList<IReadOnlyList<string>> values)
    {
        var columns = values.Count == 0 ? 0 : values[0].Count;
        var chrome = 2 * CellPaddingX + 1 + CaretRoom;
        var required = new double[columns];
        var minimum = new double[columns];
        for (var r = 0; r < values.Count; r++)
        {
            for (var c = 0; c < columns; c++)
            {
                var (smallest, largest) = _cells[r][c].MeasureCellWrap();
                required[c] = Math.Max(required[c], Math.Max(largest, MinTextWidth) + chrome);
                minimum[c] = Math.Max(minimum[c], Math.Max(smallest, MinTextWidth) + chrome);
            }
        }
        var fitted = MarkdownTables.FitColumns(required, _available - 1, minimum);
        var sum = 0.0;
        var previous = 0.0;
        for (var c = 0; c < columns; c++)
        {
            sum += fitted[c];
            // 竖线落在整像素上，不发虚
            var edge = Math.Round(sum);
            _grid.ColumnDefinitions[c].Width = new GridLength(edge - previous + (c == 0 ? 1 : 0));
            // 取整后可能比 fitted 小一点：放得下的文字不能因此换行
            var textWidth = Math.Max(fitted[c], edge - previous) - chrome;
            previous = edge;
            foreach (var row in _cells)
                row[c].Width = row[c].ApplyCellWrap(textWidth) + CaretRoom;
            var horizontal = (c < _aligns.Count ? _aligns[c] : TableAlign.Left) switch
            {
                TableAlign.Center => HorizontalAlignment.Center,
                TableAlign.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            };
            foreach (var row in _cells)
                row[c].HorizontalAlignment = horizontal;
        }
    }

    // 排版之后才知道光标的位置（列宽可能刚变）
    private void EnsureCaretVisible(MarkdownEditor cell) => Dispatcher.UIThread.Post(() =>
    {
        var view = cell.TextArea.TextView;
        if (!view.VisualLinesValid || view.TranslatePoint(default, _grid) is not { } origin)
            return;
        var x = origin.X + cell.TextArea.Caret.CalculateCaretRectangle().X;
        var viewport = _scroll.Viewport.Width;
        var offset = _scroll.Offset.X;
        if (x < offset + CellPaddingX)
            _scroll.Offset = new Vector(Math.Max(0, x - CellPaddingX), 0);
        else if (x > offset + viewport - CellPaddingX)
            _scroll.Offset = new Vector(x - viewport + CellPaddingX, 0);
    }, DispatcherPriority.Background);

    /// <summary>
    /// 单元格中的按键：Enter 到同一列下方（最后一行时到表格下一行的开头），Shift+Enter 换行，Tab 到下一格，
    /// 方向键在文字的边上时到相邻的单元格或离开表格，撤销交给外层。处理了返回 true。
    /// </summary>
    public bool HandleKey(KeyEventArgs e, int row, int column)
    {
        var cell = _cells[row][column];
        var mods = e.KeyModifiers;
        switch (e.Key)
        {
            case Key.Z when mods == KeyModifiers.Control:
                _owner.Document.UndoStack.Undo();
                break;
            case Key.Y when mods == KeyModifiers.Control:
                _owner.Document.UndoStack.Redo();
                break;
            case Key.Enter when mods == KeyModifiers.Shift:
                cell.Insert("\n");
                break;
            case Key.Enter when mods == KeyModifiers.None:
                if (row + 1 < _cells.Count)
                    FocusCell(row + 1, column);
                else
                    _owner.LeaveTable(above: false, createLine: true);
                break;
            case Key.Tab when mods == KeyModifiers.None:
                MoveNext(row, column);
                break;
            case Key.Tab when mods == KeyModifiers.Shift:
                MovePrevious(row, column);
                break;
            case Key.Left when mods == KeyModifiers.None && cell.SelectionLength == 0 && cell.CaretOffset == 0:
                MovePrevious(row, column);
                break;
            case Key.Right when mods == KeyModifiers.None && cell.SelectionLength == 0 && cell.CaretOffset == cell.Text.Length:
                MoveNext(row, column);
                break;
            case Key.Up when mods == KeyModifiers.None && cell.IsCaretOnEdgeLine(first: true):
                if (row > 0)
                    FocusCell(row - 1, column);
                else
                    _owner.LeaveTable(above: true, createLine: false);
                break;
            case Key.Down when mods == KeyModifiers.None && cell.IsCaretOnEdgeLine(first: false):
                if (row + 1 < _cells.Count)
                    FocusCell(row + 1, column);
                else
                    _owner.LeaveTable(above: false, createLine: false);
                break;
            default:
                return false;
        }
        e.Handled = true;
        return true;
    }

    private void MoveNext(int row, int column)
    {
        if (column + 1 < _cells[row].Count)
            FocusCell(row, column + 1, atEnd: false);
        else if (row + 1 < _cells.Count)
            FocusCell(row + 1, 0, atEnd: false);
        else
            _owner.LeaveTable(above: false, createLine: false);
    }

    private void MovePrevious(int row, int column)
    {
        if (column > 0)
            FocusCell(row, column - 1);
        else if (row > 0)
            FocusCell(row - 1, _cells[row - 1].Count - 1);
        else
            _owner.LeaveTable(above: true, createLine: false);
    }
}
