using System;
using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;

namespace ZulipQQ.Controls;

/// <summary>
/// 显示 <see cref="Dialogs"/> 中的对话框（ViewModel，经 ViewLocator 找到对应的 View）。
/// 点击遮罩与按 Esc 相同（由各对话框自己的 Esc 处理决定取消、关闭或先确认）。
/// 对话框从 <see cref="Dialogs"/> 中移除后先播放消失动画（上移、淡出），播完才从界面上移除。
/// </summary>
public partial class DialogLayer : UserControl
{
    public static readonly StyledProperty<IEnumerable?> DialogsProperty =
        AvaloniaProperty.Register<DialogLayer, IEnumerable?>(nameof(Dialogs));

    private static readonly TimeSpan CloseDuration = TimeSpan.FromMilliseconds(200);

    // 正在显示的对话框：跟随 Dialogs，但移除的对话框在消失动画结束后才从这里移除
    private readonly ObservableCollection<object> _shown = [];
    private INotifyCollectionChanged? _source;

    public DialogLayer()
    {
        InitializeComponent();
        Items.ItemsSource = _shown;
    }

    public IEnumerable? Dialogs
    {
        get => GetValue(DialogsProperty);
        set => SetValue(DialogsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != DialogsProperty)
            return;
        if (_source is not null)
            _source.CollectionChanged -= OnDialogsChanged;
        _source = Dialogs as INotifyCollectionChanged;
        if (_source is not null)
            _source.CollectionChanged += OnDialogsChanged;
        _shown.Clear();
        foreach (var dialog in Dialogs?.Cast<object>() ?? [])
            _shown.Add(dialog);
    }

    private void OnDialogsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var current = Dialogs?.Cast<object>().ToList() ?? [];
        // 新打开的对话框加在最上层；被移除的播放消失动画
        foreach (var dialog in current.Where(d => !_shown.Contains(d)))
            _shown.Add(dialog);
        foreach (var dialog in _shown.Where(d => !current.Contains(d)).ToList())
            _ = CloseAsync(dialog);
    }

    private async Task CloseAsync(object dialog)
    {
        if (Items.ContainerFromItem(dialog) is not Control container || !container.IsVisible || !IsEffectivelyVisible)
        {
            _shown.Remove(dialog);
            return;
        }
        // 动画期间不再响应点击，避免关闭中的对话框被再次操作
        container.IsHitTestVisible = false;
        var box = container.GetVisualDescendants().OfType<FitWidthDecorator>().FirstOrDefault();
        var moveUp = new Animation
        {
            Duration = CloseDuration,
            Easing = new CubicEaseOut(),
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(TranslateTransform.YProperty, 0d) } },
                new KeyFrame { Cue = new Cue(0.6), Setters = { new Setter(TranslateTransform.YProperty, -16d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(TranslateTransform.YProperty, -20d) } },
            },
        };
        // 先上移，再淡出（遮罩一起淡出）
        var fadeOut = new Animation
        {
            Duration = CloseDuration,
            FillMode = FillMode.Forward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(0.35), Setters = { new Setter(OpacityProperty, 1d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(OpacityProperty, 0d) } },
            },
        };
        try
        {
            await Task.WhenAll(
                box?.RenderTransform is TranslateTransform ? moveUp.RunAsync(box) : Task.CompletedTask,
                fadeOut.RunAsync(container));
        }
        finally
        {
            _shown.Remove(dialog);
        }
    }

    // 点击遮罩：向对话框发送 Esc；对话框没有处理 Esc 时，执行它的取消或关闭命令
    private void OnMaskTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not Control { Parent: Panel panel } || panel.Children.OfType<FitWidthDecorator>().FirstOrDefault() is not { } box
            || box.GetVisualDescendants().OfType<ContentControl>().FirstOrDefault()?.Presenter?.Child is not { } view)
            return;
        e.Handled = true;
        var esc = new KeyEventArgs { RoutedEvent = KeyDownEvent, Key = Key.Escape, Source = view };
        view.RaiseEvent(esc);
        if (esc.Handled || panel.DataContext is not { } dialog)
            return;
        var command = (dialog.GetType().GetProperty("CancelCommand") ?? dialog.GetType().GetProperty("CloseCommand"))
            ?.GetValue(dialog) as ICommand;
        if (command?.CanExecute(null) == true)
            command.Execute(null);
    }
}
