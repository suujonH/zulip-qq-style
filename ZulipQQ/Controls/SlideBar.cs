using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace ZulipQQ.Controls;

/// <summary>
/// 底部滑出栏（仿 QQ 好友管理器的多选操作栏）：
/// <see cref="IsOpen"/> 变为 true 时立即占据内容高度，内容从下方 <see cref="SlideOffset"/> 处滑入并淡入；
/// 变为 false 时内容向下滑出并淡出，同时占据的高度逐渐收缩为 0，上方内容随之展开。
/// 控件加载前的初始状态不播放动画。
/// </summary>
public class SlideBar : Decorator
{
    public static readonly StyledProperty<bool> IsOpenProperty =
        AvaloniaProperty.Register<SlideBar, bool>(nameof(IsOpen));

    public static readonly StyledProperty<double> SlideOffsetProperty =
        AvaloniaProperty.Register<SlideBar, double>(nameof(SlideOffset), 40);

    public static readonly StyledProperty<TimeSpan> OpenDurationProperty =
        AvaloniaProperty.Register<SlideBar, TimeSpan>(nameof(OpenDuration), TimeSpan.FromMilliseconds(150));

    public static readonly StyledProperty<TimeSpan> CloseDurationProperty =
        AvaloniaProperty.Register<SlideBar, TimeSpan>(nameof(CloseDuration), TimeSpan.FromMilliseconds(180));

    // 占据高度的比例（0..1）与内容显示进度（0 = 完全滑出隐藏，1 = 在原位完全显示）
    private double _heightFactor;
    private double _contentProgress;

    // 动画起点；IsOpen 在动画途中再次变化时从当前值继续
    private double _fromHeight;
    private double _fromContent;
    private TimeSpan? _startTime;
    private int _generation;

    public SlideBar()
    {
        ClipToBounds = true;
    }

    public bool IsOpen
    {
        get => GetValue(IsOpenProperty);
        set => SetValue(IsOpenProperty, value);
    }

    public double SlideOffset
    {
        get => GetValue(SlideOffsetProperty);
        set => SetValue(SlideOffsetProperty, value);
    }

    public TimeSpan OpenDuration
    {
        get => GetValue(OpenDurationProperty);
        set => SetValue(OpenDurationProperty, value);
    }

    public TimeSpan CloseDuration
    {
        get => GetValue(CloseDurationProperty);
        set => SetValue(CloseDurationProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == ChildProperty)
            ApplyContent();

        if (change.Property != IsOpenProperty)
            return;

        _generation++;
        _startTime = null;
        var topLevel = TopLevel.GetTopLevel(this);
        if (!IsLoaded || topLevel is null)
        {
            _heightFactor = _contentProgress = IsOpen ? 1 : 0;
            ApplyContent();
            InvalidateMeasure();
            return;
        }

        // 打开时立即占位，只有内容做滑入动画
        if (IsOpen)
            _heightFactor = 1;

        _fromHeight = _heightFactor;
        _fromContent = _contentProgress;
        var generation = _generation;
        topLevel.RequestAnimationFrame(time => OnAnimationFrame(time, generation));
        InvalidateMeasure();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        return new Size(size.Width, size.Height * _heightFactor);
    }

    // 内容始终按完整高度排列在顶部，超出部分被裁剪
    protected override Size ArrangeOverride(Size finalSize)
    {
        Child?.Arrange(new Rect(0, 0, finalSize.Width, Child.DesiredSize.Height));
        return finalSize;
    }

    private void OnAnimationFrame(TimeSpan time, int generation)
    {
        // IsOpen 已再次变化，旧动画作废
        if (generation != _generation)
            return;

        _startTime ??= time;
        var duration = (IsOpen ? OpenDuration : CloseDuration).TotalMilliseconds;
        var progress = duration <= 0 ? 1 : Math.Min(1, (time - _startTime.Value).TotalMilliseconds / duration);
        var eased = 1 - Math.Pow(1 - progress, 3); // CubicEaseOut

        if (IsOpen)
        {
            _contentProgress = _fromContent + (1 - _fromContent) * eased;
        }
        else
        {
            _contentProgress = _fromContent * (1 - eased);
            _heightFactor = _fromHeight * (1 - eased);
            InvalidateMeasure();
        }

        ApplyContent();

        if (progress < 1)
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(t => OnAnimationFrame(t, generation));
    }

    private void ApplyContent()
    {
        if (Child is null)
            return;

        Child.RenderTransform = new TranslateTransform(0, SlideOffset * (1 - _contentProgress));
        Child.Opacity = _contentProgress;
        Child.IsHitTestVisible = IsOpen;
    }
}
