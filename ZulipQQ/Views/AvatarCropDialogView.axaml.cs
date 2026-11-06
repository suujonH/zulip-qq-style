using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class AvatarCropDialogView : UserControl
{
    private AvatarCropDialogViewModel? _vm;
    private Bitmap? _bitmap;

    public AvatarCropDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is not AvatarCropDialogViewModel vm || vm == _vm)
            return;
        ReleaseBitmap();
        _vm = vm;
        _bitmap = vm.Image.ToBitmap();
        Canvas.Show(_bitmap, vm.Crop);
    }

    // 打开后焦点在保存按钮上，回车保存、Esc 取消
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => SaveButton.Focus());
    }

    // 消失动画结束或所在窗口关闭后才从界面上移除，此时才能释放正在显示的图片
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        ReleaseBitmap();
        _vm?.Dispose();
    }

    private void ReleaseBitmap()
    {
        Canvas.Show(null, null);
        _bitmap?.Dispose();
        _bitmap = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || _vm is not { } vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CancelCommand.Execute(null));
    }
}
