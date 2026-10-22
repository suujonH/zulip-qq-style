using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class CreateChannelDialogView : UserControl
{
    public CreateChannelDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => NameBox.Focus());
    }

    // Esc 取消（等按键事件处理完再执行）；描述可以多行，回车不确定
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not CreateChannelDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CancelCommand.Execute(null));
    }
}
