using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class RemarkDialogView : UserControl
{
    public RemarkDialogView()
    {
        InitializeComponent();
        RemarkBox.AddHandler(KeyDownEvent, OnRemarkBoxKeyDown, RoutingStrategies.Tunnel);
    }

    // 打开后直接可以修改：聚焦并全选已有备注
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            RemarkBox.Focus();
            RemarkBox.SelectAll();
        });
    }

    // 回车确定、Esc 取消。命令等这次按键事件处理完再执行
    private void OnRemarkBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not RemarkDialogViewModel vm)
            return;

        ICommand? command = e.Key switch
        {
            Key.Enter => vm.ConfirmCommand,
            Key.Escape => vm.CancelCommand,
            _ => null,
        };
        if (command is null)
            return;

        e.Handled = true;
        Dispatcher.UIThread.Post(() => command.Execute(null));
    }
}
