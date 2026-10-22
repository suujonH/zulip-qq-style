using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class BrowseChannelsDialogView : UserControl
{
    public BrowseChannelsDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    // Esc 关闭（等按键事件处理完再执行）
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not BrowseChannelsDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CloseCommand.Execute(null));
    }
}
