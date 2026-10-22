using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

public partial class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private int _openVersion;

    public MainViewModel(ZulipSession session)
    {
        Session = session;
        Messages = new MessagesPageViewModel(session, key => _ = OpenChatAsync(key), CloseChatAsync, ShowToast);
        Contacts = new ContactsPageViewModel(session);
        CurrentPage = Messages;

        session.Users.UserChanged += (_, id) =>
        {
            if (id == session.SelfId)
                OnSelfChanged();
        };
        session.Users.Reset += (_, _) => OnSelfChanged();
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastText = null;
        };
    }

    public ZulipSession Session { get; }

    public string SelfName => Session.Users.Get(Session.SelfId)?.FullName ?? Session.Account.FullName;

    public string? SelfAvatarUrl => Session.Users.Get(Session.SelfId)?.AvatarUrl ?? Session.Account.AvatarUrl;

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

    /// <summary>右侧内容区：聊天、资料面板等；为 null 时显示企鹅占位图标。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRightPane))]
    public partial ViewModelBase? RightPane { get; set; }

    public bool HasRightPane => RightPane is not null;

    /// <summary>窗口顶部居中的提示文字，3 秒后消失。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasToast))]
    public partial string? ToastText { get; private set; }

    public bool HasToast => ToastText is not null;

    public void ShowToast(string text)
    {
        ToastText = text;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>
    /// 打开会话：在右侧显示聊天（已在独立窗口中打开时激活该窗口），切换到消息页并选中该会话。
    /// 会话已有消息时确保它在消息列表中；没有任何消息的新会话等第一条消息发出后再由新消息加入列表。
    /// 期间又打开了别的会话时，旧的请求在 await 返回后放弃。
    /// </summary>
    public async Task OpenChatAsync(ConversationKey key, long? focusMessageId = null)
    {
        var version = ++_openVersion;
        if (Session.Registry.Activate(key, focusMessageId))
            return;

        CurrentPage = Messages;
        if (Session.Conversations.Get(key) is { LastMessageId: > 0 })
            Session.Conversations.EnsureInList(key);
        Messages.Select(key);

        if (RightPane is ChatViewModel same && same.Key.Equals(key))
        {
            if (focusMessageId is { } focus)
                await same.JumpToMessageAsync(focus);
            return;
        }

        if (RightPane is ChatViewModel old)
            await old.CloseAsync();
        if (version != _openVersion || Session.IsDisposed)
            return;

        var chat = new ChatViewModel(Session, key, (k, f) => OpenChatAsync(k, f), ShowToast);
        RightPane = chat;
        await chat.OpenAsync(focusMessageId);
    }

    /// <summary>右侧正在显示该会话时关闭聊天，恢复企鹅占位图标。</summary>
    public async Task CloseChatAsync(ConversationKey key)
    {
        if (RightPane is not ChatViewModel chat || !chat.Key.Equals(key))
            return;
        ++_openVersion;
        RightPane = null;
        Messages.Select(null);
        await chat.CloseAsync();
    }

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
        OnPropertyChanged(nameof(SelfAvatarUrl));
        OnPropertyChanged(nameof(SignatureText));
    }
}
