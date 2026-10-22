using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using ZulipQQ.Core;
using ZulipQQ.Data;

namespace ZulipQQ.Services;

/// <summary>超级调色盘的配置，以 JSON 保存在程序级设置 <c>palette</c> 中。</summary>
public sealed record ThemeConfig
{
    /// <summary>预设的强调色（<see cref="ThemeService.Presets"/> 的键）。</summary>
    public string Preset { get; init; } = "default";

    /// <summary>“随心调”选的强调色 <c>#RRGGBB</c>；不为 null 时覆盖预设。</summary>
    public string? Accent { get; init; }

    /// <summary>主窗口背景图（已复制到数据目录）；null 为不使用。</summary>
    public string? BackgroundImage { get; init; }

    /// <summary>有背景图时主面板的不透明度，0.6～1。</summary>
    public double PanelOpacity { get; init; } = 1.0;
}

/// <summary>超级调色盘的一个预设。</summary>
public sealed record ThemePreset(string Key, string Name, string Color);

/// <summary>
/// 主题：浅色 / 深色（Zulip 的 color_scheme）、字体大小、超级调色盘。
/// 调色盘的强调色和背景图透明度通过在 Application.Resources 顶层写入同名画刷覆盖主题字典中的值；
/// 移除这些键即恢复默认。主题变体变化时重新计算。只在 UI 线程上使用。
/// </summary>
public static class ThemeService
{
    public const int DefaultFontSize = 14;
    public const int MinFontSize = 12;
    public const int MaxFontSize = 18;
    public const double MinPanelOpacity = 0.6;

    public static readonly IReadOnlyList<ThemePreset> Presets =
    [
        new("default", "默认", "#0099FF"),
        new("mint", "薄荷", "#2BA471"),
        new("orange", "暖橙", "#E37318"),
        new("sakura", "樱花", "#E851B3"),
        new("grape", "葡萄", "#8E56DD"),
        new("graphite", "石墨", "#4B4B4B"),
    ];

    private const string PaletteKey = "palette";
    private const string BackgroundImageKey = "ThemeBackgroundImage";

    // 字号资源与相对正文字号的差值
    private static readonly (string Key, int Delta)[] FontSizes =
    [
        ("FontSizeLarge", 4), ("FontSizeH2", 2), ("FontSizeH3", 1), ("FontSizeBody", 0),
        ("FontSizeSmall", -1), ("FontSizeSub", -2), ("FontSizeCaption", -3),
    ];

    // 调色盘覆盖的画刷
    private static readonly string[] OverrideKeys =
    [
        "BrandBrush", "BrandHoverBrush", "BrandDisabledBrush", "OwnBubbleBrush", "OwnBubbleSelectedBrush",
        "PanelBackgroundBrush", "SidebarBackgroundBrush", BackgroundImageKey,
    ];

    private static SettingRepository? _appSettings;
    private static int _colorScheme = 1;
    private static bool _previewDark;
    private static string? _bitmapPath;
    private static Bitmap? _bitmap;

    public static ThemeConfig Palette { get; private set; } = new();

    /// <summary>调色盘配置变化（设置页据此刷新）。</summary>
    public static event EventHandler? PaletteChanged;

    private static Application App => Application.Current!;

    /// <summary>程序启动、数据库打开后调用：读取并应用调色盘。</summary>
    public static void Initialize(LocalDb db)
    {
        _appSettings = new SettingRepository(db, null);
        Palette = Load(_appSettings.Get(PaletteKey));
        App.ActualThemeVariantChanged += (_, _) => ApplyPaletteResources();
        ApplyPaletteResources();
    }

    /// <summary>预设或“随心调”的强调色。</summary>
    public static string AccentOf(ThemeConfig config) =>
        config.Accent ?? Presets.FirstOrDefault(p => p.Key == config.Preset)?.Color ?? Presets[0].Color;

    /// <summary>Zulip 的 color_scheme：1 跟随系统、2 深色、3 浅色。“预览夜间”打开时暂不生效。</summary>
    public static void ApplyColorScheme(int colorScheme)
    {
        _colorScheme = colorScheme;
        ApplyVariant();
    }

    /// <summary>“预览夜间”：临时切换为深色，不修改 Zulip 设置。</summary>
    public static void SetPreviewDark(bool on)
    {
        _previewDark = on;
        ApplyVariant();
    }

    /// <summary>正文字号（12～18），其它字号资源按固定差值随之变化。</summary>
    public static void ApplyFontSize(int size)
    {
        size = Math.Clamp(size, MinFontSize, MaxFontSize);
        foreach (var (key, delta) in FontSizes)
            App.Resources[key] = (double)(size + delta);
    }

    /// <summary>应用并保存调色盘配置。</summary>
    public static void SetPalette(ThemeConfig config)
    {
        Palette = config with { PanelOpacity = Math.Clamp(config.PanelOpacity, MinPanelOpacity, 1) };
        _appSettings?.Set(PaletteKey, JsonSerializer.Serialize(Palette));
        ApplyPaletteResources();
        PaletteChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>把选中的图片复制到数据目录（替换旧背景图），返回新路径。</summary>
    public static string ImportBackgroundImage(string sourcePath)
    {
        var dir = Path.Combine(AppPaths.DataRoot, "theme");
        Directory.CreateDirectory(dir);
        // 每次换一个文件名：旧的位图可能仍被引用，不能原地覆盖
        var target = Path.Combine(dir, $"background-{DateTime.Now:yyyyMMddHHmmss}{Path.GetExtension(sourcePath)}");
        File.Copy(sourcePath, target, overwrite: true);
        foreach (var old in Directory.EnumerateFiles(dir, "background*").Where(f => f != target && f != Palette.BackgroundImage))
            TryDelete(old);
        return target;
    }

    private static void ApplyVariant()
    {
        App.RequestedThemeVariant = _previewDark ? ThemeVariant.Dark : _colorScheme switch
        {
            2 => ThemeVariant.Dark,
            3 => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
    }

    private static void ApplyPaletteResources()
    {
        var resources = App.Resources;
        foreach (var key in OverrideKeys)
            resources.Remove(key);

        var dark = App.ActualThemeVariant == ThemeVariant.Dark;
        if (Rgb.TryParse(AccentOf(Palette), out var accent) && accent.ToHex() != Presets[0].Color)
        {
            resources["BrandBrush"] = Brush(accent);
            resources["BrandHoverBrush"] = Brush(ColorMath.Lighten(accent, 0.1));
            resources["BrandDisabledBrush"] = Brush(dark ? ColorMath.Mix(accent, Rgb.Black, 0.6) : ColorMath.Mix(accent, Rgb.White, 0.65));
            resources["OwnBubbleBrush"] = Brush(dark ? ColorMath.Mix(accent, Rgb.Black, 0.55) : ColorMath.Mix(accent, Rgb.White, 0.8));
            resources["OwnBubbleSelectedBrush"] = Brush(dark ? ColorMath.Mix(accent, Rgb.Black, 0.45) : ColorMath.Mix(accent, Rgb.White, 0.7));
        }

        if (LoadBitmap(Palette.BackgroundImage) is not { } bitmap)
            return;
        resources[BackgroundImageKey] = bitmap;
        if (Palette.PanelOpacity >= 1)
            return;
        // 主面板半透明，露出下面的背景图
        foreach (var key in (string[])["PanelBackgroundBrush", "SidebarBackgroundBrush"])
        {
            if (App.TryGetResource(key, App.ActualThemeVariant, out var value) && value is ISolidColorBrush b)
                resources[key] = new SolidColorBrush(Color.FromArgb((byte)Math.Round(255 * Palette.PanelOpacity), b.Color.R, b.Color.G, b.Color.B));
        }
    }

    private static Bitmap? LoadBitmap(string? path)
    {
        if (path == _bitmapPath)
            return _bitmap;
        _bitmapPath = path;
        _bitmap = null;
        if (path is null || !File.Exists(path))
            return null;
        try
        {
            using var stream = File.OpenRead(path);
            _bitmap = new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warn($"加载背景图失败：{path}", ex);
        }
        return _bitmap;
    }

    private static ThemeConfig Load(string? json)
    {
        if (string.IsNullOrEmpty(json))
            return new ThemeConfig();
        try
        {
            return JsonSerializer.Deserialize<ThemeConfig>(json) ?? new ThemeConfig();
        }
        catch (JsonException ex)
        {
            Log.Warn("调色盘配置无效，使用默认值", ex);
            return new ThemeConfig();
        }
    }

    private static SolidColorBrush Brush(Rgb c) => new(Color.FromRgb(c.R, c.G, c.B));

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
