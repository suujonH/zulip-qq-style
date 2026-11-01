using Avalonia.Controls;
using Avalonia.Threading;

namespace ZulipQQ.Views;

public partial class EmojiPickerView : UserControl
{
    public EmojiPickerView()
    {
        InitializeComponent();
    }

    // 弹出后直接可以输入搜索
    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }
}
