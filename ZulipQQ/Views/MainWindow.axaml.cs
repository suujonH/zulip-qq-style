using Avalonia.Controls;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class MainWindow : Window, ISessionWindow
{
    public MainWindow()
    {
        InitializeComponent();
    }

    // 用户关闭主面板即退出程序；退出账号、程序退出或系统关机时由 App 关闭，不拦截
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (App.IsEndingSession || e.CloseReason is WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown)
            return;
        e.Cancel = true;
        _ = App.Current.ExitAsync();
    }

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaximizeClick(object? sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    // “+”菜单：没有权限的项不显示，所以每次打开时生成
    private void OnAddClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm || sender is not Control button)
            return;
        var flyout = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Items.Add(ConversationMenus.Item("发起群聊", vm.ShowGroupChatPicker));
        flyout.Items.Add(ConversationMenus.Item("新建话题", vm.ShowNewTopicDialog));
        flyout.Items.Add(ConversationMenus.Item("加入频道", vm.ShowBrowseChannels));
        if (vm.CanCreateChannel)
            flyout.Items.Add(ConversationMenus.Item("创建频道", vm.ShowCreateChannelDialog));
        flyout.ShowAt(button);
    }
}
