using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>调色盘预设色卡。</summary>
public sealed partial class PresetItemViewModel(ThemePreset preset) : ObservableObject
{
    public ThemePreset Preset { get; } = preset;

    public string Name => Preset.Name;

    public IBrush Fill { get; } = new SolidColorBrush(Color.Parse(preset.Color));

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}

/// <summary>
/// 超级调色盘：恢复默认、预览夜间（临时，不改 Zulip 设置，离开本页时恢复）、随心调强调色、背景图与面板不透明度、预设色卡。
/// 配置保存在本机（程序级，所有账号共用）。
/// </summary>
public sealed partial class PalettePageViewModel : SettingsPageViewModel
{
    private bool _updating;

    public PalettePageViewModel(SettingsViewModel owner) : base(owner)
    {
        Presets = ThemeService.Presets.Select(p => new PresetItemViewModel(p)).ToList();
        ThemeService.PaletteChanged += OnPaletteChanged;
        Refresh();
    }

    public IReadOnlyList<PresetItemViewModel> Presets { get; }

    [ObservableProperty]
    public partial bool PreviewDark { get; set; }

    /// <summary>当前强调色（随心调）。</summary>
    [ObservableProperty]
    public partial Color AccentColor { get; set; }

    [ObservableProperty]
    public partial bool HasBackground { get; private set; }

    [ObservableProperty]
    public partial double PanelOpacity { get; set; }

    public double MinPanelOpacity => ThemeService.MinPanelOpacity;

    partial void OnPreviewDarkChanged(bool value) => ThemeService.SetPreviewDark(value);

    partial void OnAccentColorChanged(Color value)
    {
        if (_updating)
            return;
        var hex = new Rgb(value.R, value.G, value.B).ToHex();
        if (hex != ThemeService.AccentOf(ThemeService.Palette))
            ThemeService.SetPalette(ThemeService.Palette with { Accent = hex });
    }

    partial void OnPanelOpacityChanged(double value)
    {
        if (!_updating && Math.Abs(value - ThemeService.Palette.PanelOpacity) > 0.001)
            ThemeService.SetPalette(ThemeService.Palette with { PanelOpacity = value });
    }

    [RelayCommand]
    private void SelectPreset(PresetItemViewModel item) =>
        ThemeService.SetPalette(ThemeService.Palette with { Preset = item.Preset.Key, Accent = null });

    /// <summary>恢复默认：清空调色盘配置（含背景图），关闭夜间预览。</summary>
    [RelayCommand]
    private void Reset()
    {
        PreviewDark = false;
        ThemeService.SetPalette(new ThemeConfig());
    }

    [RelayCommand]
    private void ClearBackground() => ThemeService.SetPalette(ThemeService.Palette with { BackgroundImage = null });

    /// <summary>使用选择的图片作为主窗口背景（由窗口弹出文件选择器）。</summary>
    public void SetBackground(string sourcePath)
    {
        try
        {
            var path = ThemeService.ImportBackgroundImage(sourcePath);
            // 第一次设置背景时面板默认稍微透明，否则看不到背景
            var opacity = ThemeService.Palette.BackgroundImage is null && ThemeService.Palette.PanelOpacity >= 1 ? 0.85 : ThemeService.Palette.PanelOpacity;
            ThemeService.SetPalette(ThemeService.Palette with { BackgroundImage = path, PanelOpacity = opacity });
        }
        catch (Exception ex)
        {
            Log.Warn($"设置背景图失败：{sourcePath}", ex);
            Owner.ShowToast("无法使用该图片");
        }
    }

    /// <summary>离开本页时结束夜间预览。</summary>
    public override void OnLeave() => PreviewDark = false;

    public override void Close()
    {
        ThemeService.PaletteChanged -= OnPaletteChanged;
        if (PreviewDark)
            ThemeService.SetPreviewDark(false);
    }

    private void OnPaletteChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        _updating = true;
        var palette = ThemeService.Palette;
        foreach (var p in Presets)
            p.IsSelected = palette.Accent is null && p.Preset.Key == palette.Preset;
        if (Rgb.TryParse(ThemeService.AccentOf(palette), out var accent))
            AccentColor = Color.FromRgb(accent.R, accent.G, accent.B);
        HasBackground = palette.BackgroundImage is not null;
        PanelOpacity = palette.PanelOpacity;
        _updating = false;
    }
}
