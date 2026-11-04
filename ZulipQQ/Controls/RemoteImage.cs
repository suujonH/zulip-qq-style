using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZulipQQ.Services;

namespace ZulipQQ.Controls;

/// <summary>
/// 通过 <see cref="ImageCache"/> 加载的图片，大小由 Width / Height 指定，圆角裁剪、等比填充。
/// 加载中显示灰色占位，失败时在占位上显示提示。设置了 <see cref="Href"/> 时可点击。
/// </summary>
public class RemoteImage : Control
{
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Source));

    public static readonly StyledProperty<string?> HrefProperty =
        AvaloniaProperty.Register<RemoteImage, string?>(nameof(Href));

    public static readonly StyledProperty<double> CornerRadiusProperty =
        AvaloniaProperty.Register<RemoteImage, double>(nameof(CornerRadius), 6);

    private Bitmap? _bitmap;
    private bool _failed;
    private bool _openOnDoubleClick;
    private IBrush? _selectionOverlay;

    static RemoteImage()
    {
        AffectsRender<RemoteImage>(CornerRadiusProperty);
    }

    public RemoteImage()
    {
        // 占位底色取自主题
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public double CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>点击时打开的地址（原图或原链接）；为 null 时不可点击。</summary>
    public string? Href
    {
        get => GetValue(HrefProperty);
        set => SetValue(HrefProperty, value);
    }

    /// <summary>消息中的图片：单击与点在其它地方相同，双击才打开 <see cref="Href"/>。</summary>
    public bool OpenOnDoubleClick
    {
        get => _openOnDoubleClick;
        set
        {
            _openOnDoubleClick = value;
            UpdateCursor();
        }
    }

    /// <summary>被选中时盖在图片上的颜色（所在段落的选择色）；null 为没有选中。</summary>
    public IBrush? SelectionOverlay
    {
        get => _selectionOverlay;
        set
        {
            if (_selectionOverlay == value)
                return;
            _selectionOverlay = value;
            InvalidateVisual();
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == RealmImage.ClientProperty)
            Load();
        else if (change.Property == HrefProperty)
            UpdateCursor();
    }

    private void UpdateCursor() => Cursor = Href is null || OpenOnDoubleClick ? null : HandCursor;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_bitmap is null && !_failed)
            Load();
    }

    // 段落文字中的图片：单击与点在文字上相同（不选中图片）；双击打开，代替段落的双击选词
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!OpenOnDoubleClick || e.ClickCount != 2 || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || Href is null || Source is null)
            return;
        e.Handled = true;
        RaiseEvent(new MessageLinkEventArgs(MessageContent.LinkClickedEvent, new ImageTarget(Source, Href)));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (OpenOnDoubleClick || Href is null || e.InitialPressMouseButton != MouseButton.Left || Source is null)
            return;
        e.Handled = true;
        RaiseEvent(new MessageLinkEventArgs(MessageContent.LinkClickedEvent, new ImageTarget(Source, Href)));
    }

    private async void Load()
    {
        var source = Source;
        _bitmap = null;
        _failed = false;
        InvalidateVisual();
        if (string.IsNullOrEmpty(source))
            return;

        // 按显示宽度的 2 倍解码，高 DPI 下也清晰
        var decodeWidth = double.IsNaN(Width) ? null : (int?)Math.Ceiling(Width * 2);
        var client = RealmImage.GetClient(this);
        var bitmap = ImageCache.Shared.TryGet(source, decodeWidth, client) ?? await ImageCache.Shared.GetAsync(source, decodeWidth, client);
        if (Source != source || RealmImage.GetClient(this) != client)
            return;
        _bitmap = bitmap;
        _failed = bitmap is null;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var rect = new Rect(Bounds.Size);
        if (rect.Width <= 0 || rect.Height <= 0)
            return;

        using (context.PushClip(new RoundedRect(rect, CornerRadius)))
        {
            if (_bitmap is null)
            {
                context.FillRectangle(Brush("FillBrush", Brushes.LightGray), rect);
                if (_failed && rect.Width > 60)
                {
                    var text = new FormattedText("图片加载失败", System.Globalization.CultureInfo.CurrentUICulture,
                        FlowDirection.LeftToRight, Typeface.Default, 12, Brush("TextSecondaryBrush", Brushes.Gray));
                    context.DrawText(text, new Point((rect.Width - text.Width) / 2, (rect.Height - text.Height) / 2));
                }
                return;
            }

            // 等比填充：裁掉超出的部分
            var size = _bitmap.Size;
            var scale = Math.Max(rect.Width / size.Width, rect.Height / size.Height);
            var w = rect.Width / scale;
            var h = rect.Height / scale;
            var src = new Rect((size.Width - w) / 2, (size.Height - h) / 2, w, h);
            context.DrawImage(_bitmap, src, rect);
            if (SelectionOverlay is { } overlay)
            {
                using (context.PushOpacity(0.4))
                    context.FillRectangle(overlay, rect);
            }
        }
    }

    private IBrush Brush(string key, IBrush fallback) =>
        this.TryFindResource(key, ActualThemeVariant, out var value) && value is IBrush brush ? brush : fallback;
}
