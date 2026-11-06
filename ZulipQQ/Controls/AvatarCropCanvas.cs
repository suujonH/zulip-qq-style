using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZulipQQ.Core;

namespace ZulipQQ.Controls;

/// <summary>
/// 头像裁剪的取景区：白底（透明图片与头像控件的显示一致）上画图片，圆形以外压暗。
/// 拖动移动图片，滚轮以指针位置为中心缩放。
/// </summary>
public sealed class AvatarCropCanvas : Control
{
    private const double WheelStep = 0.08;

    private static readonly IBrush Mask = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0));

    private AvatarCrop? _crop;
    private Bitmap? _source;
    private Point? _dragFrom;

    public AvatarCropCanvas()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.SizeAll);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.HighQuality);
    }

    /// <summary>设置要显示的图片和裁剪状态。图片的释放由调用方负责，释放前先传 null。</summary>
    public void Show(Bitmap? source, AvatarCrop? crop)
    {
        if (_crop is not null)
            _crop.Changed -= OnCropChanged;
        _source = source;
        _crop = crop;
        if (_crop is not null)
            _crop.Changed += OnCropChanged;
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var side = _crop?.ViewportSide ?? 0;
        return new Size(side, side);
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        context.FillRectangle(Brushes.White, bounds);
        if (_source is null || _crop is null)
            return;

        var (x, y, width, height) = _crop.GetImageRect();
        context.DrawImage(_source, new Rect(_source.Size), new Rect(x, y, width, height));
        context.DrawGeometry(Mask, null, new CombinedGeometry(GeometryCombineMode.Exclude,
            new RectangleGeometry(bounds), new EllipseGeometry(bounds)));
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        _dragFrom = e.GetPosition(this);
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragFrom is not { } from)
            return;
        var position = e.GetPosition(this);
        _dragFrom = position;
        _crop?.Pan(position.X - from.X, position.Y - from.Y);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragFrom = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragFrom = null;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (_crop is null)
            return;
        var position = e.GetPosition(this);
        _crop.SetZoom(_crop.ZoomPosition + e.Delta.Y * WheelStep, position.X, position.Y);
        e.Handled = true;
    }

    private void OnCropChanged(object? sender, EventArgs e) => InvalidateVisual();
}
