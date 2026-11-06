using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;

namespace ZulipQQ.Views;

public partial class InsertTableDialogView : UserControl
{
    public InsertTableDialogView()
    {
        InitializeComponent();
    }

    // 打开后直接输入列数
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() =>
        {
            ColumnsBox.Focus();
            ColumnsBox.SelectAll();
        });
    }
}
