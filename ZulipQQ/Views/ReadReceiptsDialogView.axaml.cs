using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class ReadReceiptsDialogView : UserControl
{
    public ReadReceiptsDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    // 打开后把焦点放进对话框，Esc 才能送到这里
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => CloseButton.Focus());
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ReadReceiptsDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CloseCommand.Execute(null));
    }
}
