using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class UserPickerDialogView : UserControl
{
    public UserPickerDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    // Esc 取消；与添加分组对话框相同，等按键事件处理完再执行
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not UserPickerDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CancelCommand.Execute(null));
    }
}
