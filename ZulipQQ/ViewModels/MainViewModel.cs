using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    public MainViewModel(ZulipSession session)
    {
        Session = session;
        Messages = new MessagesPageViewModel(session);
        Contacts = new ContactsPageViewModel(session);
        CurrentPage = Messages;

        session.Users.UserChanged += (_, id) =>
        {
            if (id == session.SelfId)
                OnSelfChanged();
        };
        session.Users.Reset += (_, _) => OnSelfChanged();
    }

    public ZulipSession Session { get; }

    public string SelfName => Session.Users.Get(Session.SelfId)?.FullName ?? Session.Account.FullName;

    /// <summary>签名为空时显示引导文字（与 QQ 一致）。</summary>
    public string SignatureText => Session.Users.GetStatusText(Session.SelfId) is { Length: > 0 } text ? text : "编辑个性签名";

    public MessagesPageViewModel Messages { get; }

    public ContactsPageViewModel Contacts { get; }

    /// <summary>打开中的对话框，后打开的显示在上层。</summary>
    public ObservableCollection<ViewModelBase> Dialogs { get; } = [];

    /// <summary>左侧导航当前选中的页面，显示在中间栏。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsMessagesActive), nameof(IsContactsActive))]
    public partial ViewModelBase CurrentPage { get; set; }

    public bool IsMessagesActive => CurrentPage == Messages;

    public bool IsContactsActive => CurrentPage == Contacts;

    /// <summary>显示确认对话框，返回用户是否点击了确定。</summary>
    public Task<bool> ConfirmAsync(string message, string confirmText = "确定", bool isDanger = false)
    {
        var tcs = new TaskCompletionSource<bool>();
        ConfirmDialogViewModel dialog = null!;
        dialog = new ConfirmDialogViewModel(message, confirmText, isDanger, confirmed =>
        {
            Dialogs.Remove(dialog);
            tcs.TrySetResult(confirmed);
        });
        Dialogs.Add(dialog);
        return tcs.Task;
    }

    [RelayCommand]
    private void ShowMessages() => CurrentPage = Messages;

    [RelayCommand]
    private void ShowContacts() => CurrentPage = Contacts;

    [RelayCommand]
    private Task LogoutAsync() => App.Current.LogoutAsync(confirm: true);

    private void OnSelfChanged()
    {
        OnPropertyChanged(nameof(SelfName));
        OnPropertyChanged(nameof(SignatureText));
    }
}
