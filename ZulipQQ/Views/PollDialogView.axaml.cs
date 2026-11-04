using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZulipQQ.Views;

public partial class PollDialogView : UserControl
{
    public PollDialogView()
    {
        InitializeComponent();
    }

    // 打开后直接输入问题
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => QuestionBox.Focus());
    }
}
