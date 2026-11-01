using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class GeneralPageView : UserControl
{
    private TopLevel? _top;

    public GeneralPageView()
    {
        InitializeComponent();
        // 窗口本身不能获得焦点：输入框失去焦点时把焦点交给页面；点击页面空白处时 Avalonia 也会把焦点交给它
        Focusable = true;
        TitleBox.AddHandler(KeyDownEvent, OnTitleBoxKeyDown, RoutingStrategies.Tunnel);
        TitleTokens.AddHandler(Button.ClickEvent, OnTokenClick);
    }

    // 话题标题在回车、在输入框外按下鼠标（包括设置窗口的其它位置）、失去焦点或离开页面时提交，输入过程中不刷新标题
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(PointerPressedEvent, OnTopPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CommitTitle();
        _top?.RemoveHandler(PointerPressedEvent, OnTopPointerPressed);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnTitleBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        CommitTitle();
        // 焦点交给页面，输入框不再显示光标
        Focus();
    }

    // 在输入框外按下鼠标：提交并让输入框失去焦点（不再显示光标和选中）；变量按钮除外，它要插入到光标处
    private void OnTopPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!TitleBox.IsKeyboardFocusWithin || e.Source is not Visual source || source == TitleBox || TitleBox.IsVisualAncestorOf(source)
            || source == TitleTokens || TitleTokens.IsVisualAncestorOf(source))
            return;
        CommitTitle();
        TitleBox.SelectionStart = TitleBox.SelectionEnd = TitleBox.CaretIndex;
        // 点在可获得焦点的控件上时焦点随后交给它
        Focus();
    }

    // 变量按钮：替换选中的文字或插入到光标处；输入框没有焦点时追加到末尾。超出长度限制时不插入
    private void OnTokenClick(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Control)?.FindAncestorOfType<Button>(includeSelf: true)?.Tag is not string token)
            return;
        var text = TitleBox.Text ?? "";
        int start = text.Length, end = text.Length;
        if (TitleBox.IsKeyboardFocusWithin)
        {
            start = Math.Clamp(Math.Min(TitleBox.SelectionStart, TitleBox.SelectionEnd), 0, text.Length);
            end = Math.Clamp(Math.Max(TitleBox.SelectionStart, TitleBox.SelectionEnd), 0, text.Length);
        }
        var result = text[..start] + token + text[end..];
        if (result.Length > TitleBox.MaxLength)
            return;
        TitleBox.Text = result;
        TitleBox.Focus();
        TitleBox.SelectionStart = TitleBox.SelectionEnd = TitleBox.CaretIndex = start + token.Length;
    }

    private void CommitTitle()
    {
        if (DataContext is GeneralPageViewModel vm)
            vm.TitleFormat = TitleBox.Text ?? "";
    }
}
