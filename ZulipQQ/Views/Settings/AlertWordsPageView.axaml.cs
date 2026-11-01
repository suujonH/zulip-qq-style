using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels.Settings;

namespace ZulipQQ.Views.Settings;

public partial class AlertWordsPageView : UserControl
{
    public AlertWordsPageView()
    {
        InitializeComponent();
        WordBox.AddHandler(KeyDownEvent, OnWordBoxKeyDown, RoutingStrategies.Tunnel);
    }

    // 回车添加
    private void OnWordBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not AlertWordsPageViewModel vm)
            return;
        e.Handled = true;
        if (vm.AddCommand.CanExecute(null))
            vm.AddCommand.Execute(null);
    }
}
