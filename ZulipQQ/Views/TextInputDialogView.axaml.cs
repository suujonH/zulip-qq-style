using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class TextInputDialogView : UserControl
{
    public TextInputDialogView()
    {
        InitializeComponent();
        InputBox.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    // 打开后直接可以输入，原有文字全选
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        });
    }

    // 回车确定、Esc 取消；与添加分组对话框相同，等这次按键处理完再执行命令
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not TextInputDialogViewModel vm)
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
        Dispatcher.UIThread.Post(() =>
        {
            if (command.CanExecute(null))
                command.Execute(null);
        });
    }
}
