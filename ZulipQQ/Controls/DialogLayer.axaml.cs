using System.Collections;
using Avalonia;
using Avalonia.Controls;

namespace ZulipQQ.Controls;

/// <summary>显示 <see cref="Dialogs"/> 中的对话框（ViewModel，经 ViewLocator 找到对应的 View）。</summary>
public partial class DialogLayer : UserControl
{
    public static readonly StyledProperty<IEnumerable?> DialogsProperty =
        AvaloniaProperty.Register<DialogLayer, IEnumerable?>(nameof(Dialogs));

    public DialogLayer()
    {
        InitializeComponent();
    }

    public IEnumerable? Dialogs
    {
        get => GetValue(DialogsProperty);
        set => SetValue(DialogsProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == DialogsProperty)
            Items.ItemsSource = Dialogs;
    }
}
