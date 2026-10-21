using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// TDesign 图标的几何数据（24x24 网格）。
/// TDesign 的线框图标是按描边（stroke）绘制的，实心图标是按填充（fill）绘制的，因此两部分分开保存。
/// </summary>
public sealed class TdIconData
{
    public Geometry? StrokeData { get; set; }

    public Geometry? FillData { get; set; }
}
