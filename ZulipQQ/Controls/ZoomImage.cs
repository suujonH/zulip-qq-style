using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace ZulipQQ.Controls;

/// <summary>
/// 可缩放、拖动的图片（图片查看器）。滚轮以鼠标位置为中心缩放（0.1～10 倍，每格放大或缩小当前的 20%），
/// 左键拖动平移，双击在“适应窗口”和 1:1 之间切换。可以向左旋转 90°（只改变显示，换图片时恢复）。
/// 新图片和窗口尺寸变化时，处于“适应窗口”状态则重新适应（不放大小图），否则保持比例、图片跟着中心移动。
/// </summary>
public class ZoomImage : Control
{
    public const double MinScale = 0.1;
    public const double MaxScale = 10;

    // 每次放大 / 缩小当前比例的 20%
    private const double ZoomInStep = 1.2;
    private const double ZoomOutStep = 0.8;

    public static readonly StyledProperty<Bitmap?> SourceProperty =
        AvaloniaProperty.Register<ZoomImage, Bitmap?>(nameof(Source));

    private double _scale = 1;
    // 图片显示区域（旋转后）的左上角
    private Vector _offset;
    // 向左旋转的次数（0～3）
    private int _rotation;

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

    /// <summary>用户调整了缩放（滚轮、双击、放大 / 缩小 / 1:1 / 适应窗口）；换图片、窗口尺寸变化时的自动适应不算。</summary>
    public event EventHandler? Zoomed;

    /// <summary>适应窗口：图片完整显示并居中，小图保持原始大小。</summary>
    public void Fit()
    {
        ApplyFit();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyFit()
    {
        _fit = true;
        if (Source is null || Bounds.Width <= 0 || Bounds.Height <= 0)
            return;
        var size = DisplaySize;
        SetScale(Math.Min(1, Math.Min(Bounds.Width / size.Width, Bounds.Height / size.Height)));
        Center();
    }

    /// <summary>原始大小（1:1），居中显示。</summary>
    public void ActualSize()
    {
        _fit = false;
        SetScale(1);
        Center();
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>向左旋转 90°，比例不变，居中显示；处于“适应窗口”状态时重新适应。</summary>
    public void RotateLeft()
    {
        _rotation = (_rotation + 1) % 4;
        if (_fit)
            ApplyFit();
        else
            Center();
    }

    /// <summary>以控件中心为基准放大当前比例的 20%。</summary>
    public void ZoomIn() => ZoomAt(ZoomInStep, new Point(Bounds.Width / 2, Bounds.Height / 2));

    /// <summary>以控件中心为基准缩小当前比例的 20%。</summary>
    public void ZoomOut() => ZoomAt(ZoomOutStep, new Point(Bounds.Width / 2, Bounds.Height / 2));

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
        Zoomed?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty)
        {
            _rotation = 0;
            ApplyFit();
        }
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_fit)
        {
            ApplyFit();
        }
        else
        {
            // 缩放或拖动过：图片跟着控件中心移动，原来在中间的仍在中间
            _offset += new Vector((e.NewSize.Width - e.PreviousSize.Width) / 2, (e.NewSize.Height - e.PreviousSize.Height) / 2);
            InvalidateVisual();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        // 触摸板的滚动量可能不是整格：按格数换算
        var delta = e.Delta.Y;
        if (delta != 0)
            ZoomAt(delta > 0 ? Math.Pow(ZoomInStep, delta) : Math.Pow(ZoomOutStep, -delta), e.GetPosition(this));
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
        // 以显示区域的中心为原点旋转后绘制
        var size = bitmap.Size;
        var display = DisplaySize;
        var center = new Point(_offset.X + display.Width * _scale / 2, _offset.Y + display.Height * _scale / 2);
        var dest = new Rect(-size.Width * _scale / 2, -size.Height * _scale / 2, size.Width * _scale, size.Height * _scale);
        using (context.PushTransform(Matrix.CreateRotation(-Math.PI / 2 * _rotation) * Matrix.CreateTranslation(center.X, center.Y)))
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
            context.DrawImage(bitmap, new Rect(size), dest);
    }

    // 旋转后的图片尺寸
    private Size DisplaySize => Source is not { } bitmap ? default
        : _rotation % 2 == 0 ? bitmap.Size : new Size(bitmap.Size.Height, bitmap.Size.Width);

    private void Center()
    {
        if (Source is null)
            return;
        var size = DisplaySize;
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
