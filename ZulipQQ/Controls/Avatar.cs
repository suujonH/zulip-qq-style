using System;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.Controls;

/// <summary>
/// 头像：有 <see cref="Source"/> 时异步加载图片并裁成圆形；加载完成前或失败时显示文字头像
/// （圆形色块 + 名称首字，颜色由名称稳定地映射到 TDesign 色板，或由 <see cref="Fill"/> 指定）。
/// </summary>
public class Avatar : Control
{
    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#0052D9")),
        new SolidColorBrush(Color.Parse("#2BA471")),
        new SolidColorBrush(Color.Parse("#E37318")),
        new SolidColorBrush(Color.Parse("#D54941")),
        new SolidColorBrush(Color.Parse("#8E56DD")),
        new SolidColorBrush(Color.Parse("#029CD4")),
        new SolidColorBrush(Color.Parse("#E851B3")),
    ];

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Text));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<Avatar>();

    /// <summary>图片地址（组织内的相对路径或完整地址）。</summary>
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<Avatar, string?>(nameof(Source));

    /// <summary>文字头像的背景色；为 null 时按名称取色板颜色。</summary>
    public static readonly StyledProperty<IBrush?> FillProperty =
        AvaloniaProperty.Register<Avatar, IBrush?>(nameof(Fill));

    private Bitmap? _bitmap;

    // _bitmap 属于谁（列表复用容器时换了数据）、来自哪个账号：同一数据换了地址时保留旧图直到新图加载完
    private object? _bitmapContext;
    private ZulipClient? _bitmapClient;

    static Avatar()
    {
        AffectsRender<Avatar>(TextProperty, FontFamilyProperty, FillProperty);
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public IBrush? Fill
    {
        get => GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize) => new(40, 40);

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == RealmImage.ClientProperty)
            LoadSource();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        // 加载失败（例如还没有设置所属账号的客户端）时，重新挂载时再试一次
        if (_bitmap is null)
            LoadSource();
    }

    private async void LoadSource()
    {
        var source = Source;
        var client = RealmImage.GetClient(this);
        var context = DataContext;
        // 同一个人换了头像时不闪成文字头像：旧图留到新图加载完
        if (string.IsNullOrEmpty(source) || !ReferenceEquals(_bitmapContext, context) || _bitmapClient != client)
            SetBitmap(null, context, client);
        if (string.IsNullOrEmpty(source))
            return;

        // 按显示尺寸的 2 倍解码，高 DPI 下也清晰
        var size = double.IsNaN(Width) ? 40 : Width;
        var decodeWidth = (int)Math.Ceiling(size * 2);
        var bitmap = ImageCache.Shared.TryGet(source, decodeWidth, client) ?? await ImageCache.Shared.GetAsync(source, decodeWidth, client);
        if (Source != source || RealmImage.GetClient(this) != client)
            return;
        // 加载失败时不能继续显示旧图：回到文字头像
        SetBitmap(bitmap, DataContext, client);
    }

    private void SetBitmap(Bitmap? bitmap, object? context, ZulipClient? client)
    {
        _bitmapContext = context;
        _bitmapClient = client;
        if (ReferenceEquals(_bitmap, bitmap))
            return;
        _bitmap = bitmap;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        if (size <= 0)
            return;

        var text = Text ?? string.Empty;
        var center = new Point(Bounds.Width / 2, Bounds.Height / 2);
        if (_bitmap is not null)
        {
            var rect = new Rect(center.X - size / 2, center.Y - size / 2, size, size);
            // 透明背景的头像（如默认的几何图案）垫一层白色圆形
            context.DrawEllipse(Brushes.White, null, center, size / 2, size / 2);
            using (context.PushGeometryClip(new EllipseGeometry(rect)))
                context.DrawImage(_bitmap, rect);
            return;
        }

        context.DrawEllipse(Fill ?? Palette[StableHash(text) % Palette.Length], null, center, size / 2, size / 2);

        if (text.Length == 0)
            return;

        // 取第一个完整字符（兼容 emoji 等代理对）
        var initial = text[..StringInfo.GetNextTextElementLength(text)].ToUpperInvariant();
        var formatted = new FormattedText(initial, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily), size * 0.42, Brushes.White);
        context.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    // string.GetHashCode 每次进程启动都会变化，这里用固定算法保证颜色稳定
    private static int StableHash(string text)
    {
        var hash = 0;
        foreach (var ch in text)
            hash = unchecked(hash * 31 + ch);
        return hash & int.MaxValue;
    }
}
