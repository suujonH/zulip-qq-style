using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using ZulipQQ.Core;

namespace ZulipQQ.Controls.MarkdownEditing;

/// <summary>
/// 编辑中的表格上方的工具栏：左边是调整大小（网格选择，缩小时多出来的内容直接丢弃）与左、中、右对齐，
/// 右边是“更多操作”下拉菜单与删除表格。按钮不取得焦点，光标留在表格中。
/// 放在 <see cref="MarkdownTableToolbarLayer"/> 中，由它定位到标题行上方留出的空白处。
/// </summary>
internal sealed class MarkdownTableToolbar : Border
{
    private const int PickerColumns = 6;
    private const int PickerRows = 10;
    private const double PickerCell = 15;

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private readonly MarkdownEditor _editor;
    private readonly TdIcon[] _alignIcons = new TdIcon[3];

    public MarkdownTableToolbar(MarkdownEditor editor)
    {
        _editor = editor;
        Height = MarkdownTableLayout.ToolbarHeight;
        // 点在工具栏空白处不移动光标
        Background = Brushes.Transparent;
        PointerPressed += (_, e) => e.Handled = true;

        var left = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var resize = IconButton("Icon.ViewModule", "调整表格大小", null);
        resize.Flyout = ResizeFlyout();
        left.Children.Add(resize);
        left.Children.Add(AlignButton(0, "Icon.AlignLeft", "左对齐", TableAlign.Left));
        left.Children.Add(AlignButton(1, "Icon.AlignCenter", "居中", TableAlign.Center));
        left.Children.Add(AlignButton(2, "Icon.AlignRight", "右对齐", TableAlign.Right));

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        var more = IconButton("Icon.MoreVertical", "更多操作", null);
        more.Flyout = MoreFlyout();
        right.Children.Add(more);
        right.Children.Add(IconButton("Icon.Delete", "删除表格", () => _editor.EditActiveTable((m, t, table, _) => MarkdownTableEdit.DeleteTable(m, t, table))));
        DockPanel.SetDock(right, Dock.Right);
        Child = new DockPanel { Children = { right, left } };
    }

    /// <summary>光标移动后更新：当前列的对齐方式。</summary>
    public void Refresh()
    {
        var align = _editor.ActiveTableAlign;
        for (var i = 0; i < _alignIcons.Length; i++)
            _alignIcons[i].Foreground = _editor.Brush((int)align == i ? "BrandBrush" : "TextSecondaryBrush");
    }

    private Button IconButton(string icon, string tip, Action? click, TdIcon? content = null)
    {
        var theme = _editor.TryFindResource("FlatButton", _editor.ActualThemeVariant, out var t) ? t as ControlTheme : null;
        var button = new Button
        {
            Theme = theme, Focusable = false, Cursor = HandCursor, Width = 24, Height = 24, Padding = new Thickness(0),
            Content = content ?? new TdIcon { IconKey = icon, Width = 16, Height = 16, Foreground = _editor.Brush("TextSecondaryBrush") },
        };
        ToolTip.SetTip(button, tip);
        if (click is not null)
            button.Click += (_, _) => click();
        return button;
    }

    private Button AlignButton(int index, string icon, string tip, TableAlign align)
    {
        _alignIcons[index] = new TdIcon { IconKey = icon, Width = 16, Height = 16 };
        return IconButton(icon, tip, () => _editor.EditActiveTable((m, t, table, caret) => MarkdownTableEdit.SetAlign(m, t, table, caret, align)),
            _alignIcons[index]);
    }

    // ───────────── 更多操作 ─────────────

    private MenuFlyout MoreFlyout()
    {
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        void Add(string header, Func<MarkdownModel, string, MarkdownTable, int, MarkdownEdit?> edit, string? gesture = null)
        {
            var item = new MenuItem { Header = header };
            if (gesture is not null)
                item.InputGesture = KeyGesture.Parse(gesture);
            item.Click += (_, _) => _editor.EditActiveTable(edit);
            flyout.Items.Add(item);
        }

        Add("上方插入行", (m, t, table, c) => MarkdownTableEdit.InsertRow(m, t, table, c, below: false));
        Add("下方插入行", (m, t, table, c) => MarkdownTableEdit.InsertRow(m, t, table, c, below: true), "Ctrl+Enter");
        Add("左侧插入列", (m, t, table, c) => MarkdownTableEdit.InsertColumn(m, t, table, c, right: false));
        Add("右侧插入列", (m, t, table, c) => MarkdownTableEdit.InsertColumn(m, t, table, c, right: true));
        flyout.Items.Add(new Separator());
        Add("上移该行", (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, -1), "Alt+Up");
        Add("下移该行", (m, t, table, c) => MarkdownTableEdit.MoveRow(m, t, table, c, 1), "Alt+Down");
        Add("左移该列", (m, t, table, c) => MarkdownTableEdit.MoveColumn(m, t, table, c, -1), "Alt+Left");
        Add("右移该列", (m, t, table, c) => MarkdownTableEdit.MoveColumn(m, t, table, c, 1), "Alt+Right");
        flyout.Items.Add(new Separator());
        Add("删除行", MarkdownTableEdit.DeleteRow, "Ctrl+Shift+Back");
        Add("删除列", MarkdownTableEdit.DeleteColumn);
        flyout.Items.Add(new Separator());
        var copy = new MenuItem { Header = "复制表格" };
        copy.Click += (_, _) => _ = _editor.CopyActiveTableAsync();
        flyout.Items.Add(copy);
        Add("格式化表格源码", MarkdownTableEdit.Format);
        flyout.Items.Add(new Separator());
        Add("删除表格", (m, t, table, _) => MarkdownTableEdit.DeleteTable(m, t, table));
        Track(flyout);
        return flyout;
    }

    // 菜单打开时焦点离开编辑器：工具栏要留着，关闭后把焦点还给编辑器
    private void Track(PopupFlyoutBase flyout)
    {
        flyout.Opening += (_, _) => _editor.SetTablePopupOpen(true);
        flyout.Closed += (_, _) => _editor.SetTablePopupOpen(false);
    }

    // ───────────── 调整大小 ─────────────

    private Flyout ResizeFlyout()
    {
        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedLeft };
        Track(flyout);
        flyout.Opening += (_, _) => flyout.Content = ResizePicker(flyout);
        return flyout;
    }

    private Control ResizePicker(Flyout flyout)
    {
        var (columns, rows) = _editor.ActiveTableSize;
        var gridColumns = Math.Max(PickerColumns, Math.Min(columns + 1, MarkdownTableEdit.MaxColumns));
        var gridRows = Math.Max(PickerRows, Math.Min(rows + 1, MarkdownTableEdit.MaxRows));
        var cells = new Border[gridRows, gridColumns];

        var columnsBox = SizeBox(columns);
        var rowsBox = SizeBox(rows);

        void Highlight(int c, int r)
        {
            for (var y = 0; y < gridRows; y++)
            {
                for (var x = 0; x < gridColumns; x++)
                {
                    var inside = x < c && y < r;
                    cells[y, x].Background = _editor.Brush(inside ? "BrandBrush" : "FillBrush");
                    // 第一行是标题：颜色深一些
                    cells[y, x].Opacity = inside ? y == 0 ? 0.8 : 0.4 : 1;
                }
            }
            columnsBox.Text = c.ToString(CultureInfo.InvariantCulture);
            rowsBox.Text = r.ToString(CultureInfo.InvariantCulture);
        }

        void Apply(int c, int r)
        {
            flyout.Hide();
            _editor.EditActiveTable((m, t, table, caret) => MarkdownTableEdit.Resize(m, t, table, caret, c, r));
        }

        var grid = new UniformGrid { Rows = gridRows, Columns = gridColumns, HorizontalAlignment = HorizontalAlignment.Center };
        for (var y = 0; y < gridRows; y++)
        {
            for (var x = 0; x < gridColumns; x++)
            {
                var (c, r) = (Math.Max(x + 1, MarkdownTableEdit.MinColumns), y + 1);
                var cell = new Border { Width = PickerCell - 2, Height = PickerCell - 2, Margin = new Thickness(1), CornerRadius = new CornerRadius(2), Cursor = HandCursor };
                cell.PointerEntered += (_, _) => Highlight(c, r);
                cell.PointerPressed += (_, e) =>
                {
                    e.Handled = true;
                    Apply(c, r);
                };
                cells[y, x] = cell;
                grid.Children.Add(cell);
            }
        }
        Highlight(columns, rows);

        void ApplyBoxes()
        {
            if (int.TryParse(columnsBox.Text, out var c) && int.TryParse(rowsBox.Text, out var r))
                Apply(c, r);
        }
        columnsBox.KeyDown += (_, e) => ApplyOnEnter(e, ApplyBoxes);
        rowsBox.KeyDown += (_, e) => ApplyOnEnter(e, ApplyBoxes);

        var times = new TextBlock { Text = "×", Margin = new Thickness(6, 0), VerticalAlignment = VerticalAlignment.Center };
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                grid,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { columnsBox, times, rowsBox },
                },
            },
        };
    }

    private static void ApplyOnEnter(KeyEventArgs e, Action apply)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        apply();
    }

    private static TextBox SizeBox(int value)
    {
        var box = new TextBox { Width = 44, MaxLength = 2, Text = value.ToString(CultureInfo.InvariantCulture), HorizontalContentAlignment = HorizontalAlignment.Center };
        box.Classes.Add("input");
        return box;
    }
}

/// <summary>放工具栏的层：盖在文字上方，只有工具栏接收指针。位置随滚动和编辑更新。</summary>
internal sealed class MarkdownTableToolbarLayer : Canvas
{
    private readonly MarkdownEditor _editor;
    private MarkdownTableToolbar? _toolbar;
    private ThemeVariant? _toolbarTheme;

    public MarkdownTableToolbarLayer(MarkdownEditor editor)
    {
        _editor = editor;
    }

    public void Update()
    {
        var textView = _editor.TextArea.TextView;
        if (_editor.ActiveTable is not { } active || !textView.VisualLinesValid
            || textView.GetVisualLine(active.Table.HeaderLine + 1) is not { } line)
        {
            if (_toolbar is not null)
                _toolbar.IsVisible = false;
            return;
        }
        var toolbar = Toolbar();
        toolbar.Width = Math.Min(Math.Max(active.Layout.Width, 200), textView.Bounds.Width);
        SetLeft(toolbar, 0);
        SetTop(toolbar, line.VisualTop - textView.VerticalOffset);
        toolbar.Refresh();
        toolbar.IsVisible = true;
    }

    // 第一次用到时才创建（按钮样式、颜色要从资源中取，构造编辑器时还取不到）；切换主题后重新创建
    private MarkdownTableToolbar Toolbar()
    {
        if (_toolbar is not null && _toolbarTheme == _editor.ActualThemeVariant)
            return _toolbar;
        if (_toolbar is not null)
            Children.Remove(_toolbar);
        _toolbar = new MarkdownTableToolbar(_editor);
        _toolbarTheme = _editor.ActualThemeVariant;
        Children.Add(_toolbar);
        return _toolbar;
    }
}
