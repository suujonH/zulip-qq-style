using System;
using Avalonia;
using Avalonia.Controls;

namespace ZulipQQ.Controls;

/// <summary>
/// 展开 / 收起容器：<see cref="IsExpanded"/> 变化时，高度在 0 与内容高度之间过渡（内容不缩放，超出部分裁掉）。
/// 收起后内容隐藏（不能获得焦点）。控件加载前的初始状态不播放动画，直接显示最终结果。
/// </summary>
public class ExpandPanel : Decorator
{
    public static readonly StyledProperty<bool> IsExpandedProperty =
        AvaloniaProperty.Register<ExpandPanel, bool>(nameof(IsExpanded));

    public static readonly StyledProperty<TimeSpan> DurationProperty =
        AvaloniaProperty.Register<ExpandPanel, TimeSpan>(nameof(Duration), TimeSpan.FromMilliseconds(180));

    // 0 = 完全收起，1 = 完全展开
    private double _progress;
    private double _from;
    private TimeSpan? _startTime;
    private bool _animating;

    public ExpandPanel()
    {
        ClipToBounds = true;
    }

    public bool IsExpanded
    {
        get => GetValue(IsExpandedProperty);
        set => SetValue(IsExpandedProperty, value);
    }

    public TimeSpan Duration
    {
        get => GetValue(DurationProperty);
        set => SetValue(DurationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChildProperty)
            UpdateChildVisibility();
        if (change.Property != IsExpandedProperty)
            return;

        _startTime = null;
        _from = _progress;
        var topLevel = TopLevel.GetTopLevel(this);
        if (IsLoaded && topLevel is not null)
        {
            UpdateChildVisibility();
            if (!_animating)
            {
                _animating = true;
                topLevel.RequestAnimationFrame(OnAnimationFrame);
            }
        }
        else
        {
            _progress = IsExpanded ? 1 : 0;
            UpdateChildVisibility();
            InvalidateMeasure();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (Child is not { } child)
            return default;
        child.Measure(availableSize);
        var eased = 1 - Math.Pow(1 - _progress, 3); // CubicEaseOut
        return new Size(child.DesiredSize.Width, child.DesiredSize.Height * eased);
    }

    // 内容始终按完整高度排列，从上往下露出
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }

    private void OnAnimationFrame(TimeSpan time)
    {
        _startTime ??= time;
        var target = IsExpanded ? 1.0 : 0.0;
        var duration = Duration.TotalMilliseconds;
        var t = duration <= 0 ? 1 : Math.Min(1, (time - _startTime.Value).TotalMilliseconds / duration);
        _progress = _from + (target - _from) * t;
        InvalidateMeasure();

        if (t < 1 && TopLevel.GetTopLevel(this) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(OnAnimationFrame);
            return;
        }
        _progress = target;
        _animating = false;
        UpdateChildVisibility();
    }

    // 展开中或已展开时显示内容；完全收起后隐藏
    private void UpdateChildVisibility()
    {
        if (Child is { } child)
            child.IsVisible = IsExpanded || _progress > 0;
    }
}
