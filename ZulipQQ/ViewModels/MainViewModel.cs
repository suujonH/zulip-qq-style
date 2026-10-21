using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel()
    {
        CurrentPage = Messages;
    }

    public User CurrentUser { get; } = MockData.CurrentUser;

    /// <summary>签名为空时显示引导文字（与 QQ 一致）。</summary>
    public string SignatureText => string.IsNullOrEmpty(CurrentUser.Signature) ? "编辑个性签名" : CurrentUser.Signature;

    public MessagesPageViewModel Messages { get; } = new();

    public ContactsPageViewModel Contacts { get; } = new();

    /// <summary>左侧导航当前选中的页面，显示在中间栏。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessagesActive), nameof(IsContactsActive))]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool IsMessagesActive => CurrentPage == Messages;

    public bool IsContactsActive => CurrentPage == Contacts;

    [RelayCommand]
    private void ShowMessages() => CurrentPage = Messages;

    [RelayCommand]
    private void ShowContacts() => CurrentPage = Contacts;
}
