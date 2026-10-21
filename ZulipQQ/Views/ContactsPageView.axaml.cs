using Avalonia.Controls;
using Avalonia.Interactivity;

namespace ZulipQQ.Views;

public partial class ContactsPageView : UserControl
{
    public ContactsPageView()
    {
        InitializeComponent();
    }

    private void OnFriendManagerClick(object? sender, RoutedEventArgs e)
    {
        if (TopLevel.GetTopLevel(this) is Window owner)
            FriendManagerWindow.ShowSingle(owner);
    }
}
