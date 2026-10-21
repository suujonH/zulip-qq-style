using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 圆形揭示容器：<see cref="IsRevealed"/> 变为 true 时，内容以“左下角为圆心、半径逐渐变大”的圆形显现（与 QQ 导航一致）；
/// 变为 false 时立即隐藏。控件加载前的初始状态不播放动画，直接显示最终结果。
/// </summary>
public class CircleReveal : Decorator
{
    public static readonly StyledProperty<bool> IsRevealedProperty =
        AvaloniaProperty.Register<CircleReveal, bool>(nameof(IsRevealed));

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<CircleReveal, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(180));

    // 0 = 完全隐藏，1 = 完全显示
    private double _progress;
    private TimeSpan? _startTime;

    public bool IsRevealed
    {
        get => GetValue(IsRevealedProperty);
        set => SetValue(IsRevealedProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property != IsRevealedProperty)
            return;

        _startTime = null;
        var topLevel = TopLevel.GetTopLevel(this);
        if (IsRevealed && IsLoaded && topLevel is not null)
        {
            _progress = 0;
            topLevel.RequestAnimationFrame(OnAnimationFrame);
        }
        else
        {
            _progress = IsRevealed ? 1 : 0;
        }

        UpdateClip();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var size = base.ArrangeOverride(finalSize);
        UpdateClip(size);
        return size;
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        // 动画途中被取消（切到别的页面），不再继续
        if (!IsRevealed || _progress >= 1)
            return;

        _startTime ??= time;
        var duration = Duration.TotalMilliseconds;
        _progress = duration <= 0 ? 1 : Math.Min(1, (time - _startTime.Value).TotalMilliseconds / duration);
        UpdateClip();

        if (_progress < 1)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnAnimationFrame);
    }

    private void UpdateClip() => UpdateClip(Bounds.Size);

    private void UpdateClip(Size size)
    {
        if (_progress >= 1)
        {
            Clip = null;
            return;
        }

        // 圆心在左下角，最大半径为对角线长度，保证结束时完全覆盖
        var center = new Point(0, size.Height);
        var maxRadius = Math.Sqrt(size.Width * size.Width + size.Height * size.Height);
        var eased = 1 - Math.Pow(1 - _progress, 3); // CubicEaseOut
        var radius = maxRadius * eased;
        Clip = new EllipseGeometry(new Rect(center.X - radius, center.Y - radius, radius * 2, radius * 2));
    }
}
