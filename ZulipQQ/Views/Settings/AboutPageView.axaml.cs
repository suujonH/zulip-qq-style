using Avalonia;
using Avalonia.Controls;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class AboutPageView : UserControl
{
    private AboutPageViewModel? _attached;

    public AboutPageView()
    {
        InitializeComponent();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = DataContext as AboutPageViewModel;
        _attached?.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached?.Detach();
        _attached = null;
    }
}
