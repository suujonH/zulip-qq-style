using Avalonia.Controls;
using Avalonia.Interactivity;
using ZulipQQ.Controls;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class SystemNoticesView : UserControl
{
    public SystemNoticesView()
    {
        InitializeComponent();
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
    }

    // 筛选：全部，以及现有通知中出现过的分类；当前分类打勾
    private void OnFilterClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SystemNoticesViewModel vm || sender is not Control anchor)
            return;
        var menu = new ContextMenu();
        foreach (var option in vm.FilterOptions)
        {
            var item = ConversationMenus.Item(option.Text, () => vm.SelectFilterCommand.Execute(option.Value));
            item.ToggleType = MenuItemToggleType.Radio;
            item.IsChecked = vm.Filter == option.Value;
            menu.Items.Add(item);
        }
        menu.Open(anchor);
    }

    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (DataContext is not SystemNoticesViewModel vm)
            return;
        e.Handled = true;
        _ = vm.HandleLinkAsync(e.Target);
    }
}
