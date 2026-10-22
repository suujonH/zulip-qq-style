using System;
using Avalonia;
using Avalonia.Controls;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class UserCardView : UserControl
{
    private UserCardViewModel? _vm;

    public UserCardView()
    {
        InitializeComponent();
    }

    // 显示期间（在可视树上）随数据变化刷新
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _vm?.Detach();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.Detach();
        _vm = DataContext as UserCardViewModel;
        if (TopLevel.GetTopLevel(this) is not null)
            _vm?.Attach();
    }
}
