using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class NewTopicDialogView : UserControl
{
    public NewTopicDialogView()
    {
        InitializeComponent();
        TopicBox.AddHandler(KeyDownEvent, OnTopicBoxKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => TopicBox.Focus());
    }

    // 回车确定、Esc 取消（与添加分组对话框相同，等按键事件处理完再执行）
    private void OnTopicBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not NewTopicDialogViewModel vm)
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
