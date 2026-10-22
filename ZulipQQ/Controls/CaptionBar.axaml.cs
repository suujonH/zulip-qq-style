using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZulipQQ.Controls;

/// <summary>无边框窗口的标题栏。按钮通过 <see cref="TopLevel.GetTopLevel"/> 找到所在窗口并操作它。</summary>
public partial class CaptionBar : UserControl
{
    public static readonly StyledProperty<bool> ShowMinimizeProperty =
        AvaloniaProperty.Register<CaptionBar, bool>(nameof(ShowMinimize), true);

    public static readonly StyledProperty<bool> ShowMaximizeProperty =
        AvaloniaProperty.Register<CaptionBar, bool>(nameof(ShowMaximize), true);

    public static readonly StyledProperty<string?> TitleProperty =
        AvaloniaProperty.Register<CaptionBar, string?>(nameof(Title));

    public CaptionBar()
    {
        InitializeComponent();
    }

    public bool ShowMinimize
    {
        get => GetValue(ShowMinimizeProperty);
        set => SetValue(ShowMinimizeProperty, value);
    }

    public bool ShowMaximize
    {
        get => GetValue(ShowMaximizeProperty);
        set => SetValue(ShowMaximizeProperty, value);
    }

    /// <summary>左侧的标题文字；为空时不显示。</summary>
    public string? Title
    {
        get => GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ShowMinimizeProperty)
            MinimizeButton.IsVisible = ShowMinimize;
        else if (change.Property == ShowMaximizeProperty)
            MaximizeButton.IsVisible = ShowMaximize;
        else if (change.Property == TitleProperty)
        {
            TitleText.Text = Title;
            TitleText.IsVisible = !string.IsNullOrEmpty(Title);
        }
    }

    private Window? HostWindow => TopLevel.GetTopLevel(this) as Window;

    private void OnMinimizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
            window.WindowState = WindowState.Minimized;
    }

    private void OnMaximizeClick(object? sender, RoutedEventArgs e)
    {
        if (HostWindow is { } window)
            window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => HostWindow?.Close();
}
