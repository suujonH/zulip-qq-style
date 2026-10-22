using Avalonia.Controls;
using Avalonia.Interactivity;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class ContactsPageView : UserControl
{
    public ContactsPageView()
    {
        InitializeComponent();
    }

    private void OnFriendManagerClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner && DataContext is ContactsPageViewModel vm)
            FriendManagerWindow.ShowSingle(owner, vm.Session);
    }
}
