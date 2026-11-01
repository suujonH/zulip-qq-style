using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ZulipQQ.Controls;

/// <summary>
/// 可缩放、拖动的图片（图片查看器）。滚轮以鼠标位置为中心缩放（0.1～8 倍），左键拖动平移，双击在“适应窗口”和 1:1 之间切换。
/// 新图片和窗口尺寸变化时，处于“适应窗口”状态则重新适应（不放大小图）。
/// </summary>
public class ZoomImage : Control
{
    public const double MinScale = 0.1;
    public const double MaxScale = 8;

    private const double WheelStep = 1.1;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    private double _scale = 1;
    private Vector _offset;

    // 处于“适应窗口”状态：尺寸变化后保持适应
    private bool _fit = true;
    private Point? _dragStart;
    private Vector _dragOffset;

    static ZoomImage()
    {
        // 透明背景也参与命中测试，空白处也能拖动和滚轮缩放
        BackgroundProperty.OverrideDefaultValue<ZoomImage>(Brushes.Transparent);
        AffectsRender<ZoomImage>(SourceProperty);
        ClipToBoundsProperty.OverrideDefaultValue<ZoomImage>(true);
    }

    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        Border.BackgroundProperty.AddOwner<ZoomImage>();

    public Bitmap? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    /// <summary>当前缩放比例。</summary>
    public double Scale => _scale;

    /// <summary>缩放比例变化（显示百分比用）。</summary>
    public event EventHandler? ScaleChanged;

    /// <summary>适应窗口：图片完整显示并居中，小图保持原始大小。</summary>
    public void Fit()
    {
        _fit = true;
        if (Source is not { } bitmap || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var size = bitmap.Size;
        SetScale(Math.Min(1, Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height)));
        Center();
    }

    /// <summary>原始大小（1:1），居中显示。</summary>
    public void ActualSize()
    {
        _fit = false;
        SetScale(1);
        Center();
    }

    /// <summary>以控件中心为基准缩放。</summary>
    public void ZoomBy(double factor) => ZoomAt(factor, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>以 <paramref name="center"/>（控件坐标）为中心缩放：该点下的图片位置保持不动。</summary>
    public void ZoomAt(double factor, Point center)
    {
        if (Source is null)
            return;
        _fit = false;
        var old = _scale;
        // 超大图适应窗口时比例可能已低于 MinScale：下限取两者较小值，缩小不会变成放大
        SetScale(Math.Clamp(old * factor, Math.Min(MinScale, old), Math.Max(MaxScale, old)));
        var ratio = _scale / old;
        _offset = new Vector(center.X - (center.X - _offset.X) * ratio, center.Y - (center.Y - _offset.Y) * ratio);
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
            Fit();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fit)
            Fit();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        ZoomAt(Math.Pow(WheelStep, e.Delta.Y), e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            return;
        if (e.ClickCount == 2)
        {
            if (_fit)
                ActualSize();
            else
                Fit();
            e.Handled = true;
            return;
        }
        _dragStart = e.GetPosition(this);
        _dragOffset = _offset;
        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragStart is not { } start)
            return;
        _fit = false;
        _offset = _dragOffset + (e.GetPosition(this) - start);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        _dragStart = null;
        e.Pointer.Capture(null);
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragStart = null;
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(Bounds.Size);
        if (Background is { } background)
            context.FillRectangle(background, bounds);
        if (Source is not { } bitmap)
            return;
        var size = bitmap.Size;
        var dest = new Rect(_offset.X, _offset.Y, size.Width * _scale, size.Height * _scale);
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(bitmap, new Rect(size), dest);
    }

    private void Center()
    {
        if (Source is not { } bitmap)
            return;
        var size = bitmap.Size;
        _offset = new Vector((Bounds.Width - size.Width * _scale) / 2, (Bounds.Height - size.Height * _scale) / 2);
        InvalidateVisual();
    }

    private void SetScale(double scale)
    {
        if (scale == _scale)
            return;
        _scale = scale;
        ScaleChanged?.Invoke(this, EventArgs.Empty);
    }
}
