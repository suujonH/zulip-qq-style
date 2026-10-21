using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class AddGroupDialogView : UserControl
{
    public AddGroupDialogView()
    {
        InitializeComponent();
        NameBox.AddHandler(KeyDownEvent, OnNameBoxKeyDown, RoutingStrategies.Tunnel);
    }

    // 打开后直接可以输入
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => NameBox.Focus());
    }

    // 回车确定、Esc 取消。命令会关闭对话框并让焦点回到之前的按钮，
    // 必须等这次按键事件处理完再执行，否则同一个回车会被那个按钮当成点击，再次打开对话框
    private void OnNameBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not AddGroupDialogViewModel vm)
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
