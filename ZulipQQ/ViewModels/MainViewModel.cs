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
    private ConversationKey? _openingKey;   // 正在等待旧聊天关闭的打开请求

    public MainViewModel(ZulipSession session)
    {
        Session = session;
        Messages = new MessagesPageViewModel(session, key => _ = OpenChatAsync(key), CloseChatAsync, ShowToast);
        Contacts = new ContactsPageViewModel(session, this);
        Topics = new TopicActions(this);
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

    /// <summary>签名的最大长度（与 QQ 一致；Zulip 上限为 60）。</summary>
    public const int SignatureMaxLength = 60;

    /// <summary>标题栏签名正在就地编辑。</summary>
    [ObservableProperty]
    public partial bool IsEditingSignature { get; private set; }

    [ObservableProperty]
    public partial string SignatureDraft { get; set; } = "";

    public MessagesPageViewModel Messages { get; }

    public ContactsPageViewModel Contacts { get; }

    /// <summary>话题操作（各处右键菜单共用）。</summary>
    public TopicActions Topics { get; }

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
        Contacts.ClearSelection();
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
        {
            _openingKey = key;
            await old.CloseAsync();
            if (version != _openVersion || Session.IsDisposed)
            {
                // 被取消（另开了会话或该会话被移除）时，不要把已关闭的旧聊天留在右侧
                if (RightPane == old)
                    RightPane = null;
                return;
            }
        }
        _openingKey = null;

        var chat = new ChatViewModel(Session, key, (k, f) => OpenChatAsync(k, f), ShowToast);
        RightPane = chat;
        await chat.OpenAsync(focusMessageId);
    }

    /// <summary>右侧正在显示（或正在打开）该会话时关闭聊天，恢复企鹅占位图标。</summary>
    public async Task CloseChatAsync(ConversationKey key)
    {
        if (_openingKey is { } opening && opening.Equals(key))
        {
            // 取消还在等待旧聊天关闭的打开请求
            ++_openVersion;
            _openingKey = null;
            Messages.Select(null);
            return;
        }
        if (RightPane is not ChatViewModel chat || !chat.Key.Equals(key))
            return;
        ++_openVersion;
        RightPane = null;
        Messages.Select(null);
        await chat.CloseAsync();
    }

    /// <summary>在右侧显示资料面板等非聊天内容；正在显示的聊天先关闭（保存草稿），进行中的打开请求取消。</summary>
    public async Task ShowPaneAsync(ViewModelBase pane)
    {
        ++_openVersion;
        _openingKey = null;
        var old = RightPane as ChatViewModel;
        RightPane = pane;
        Messages.Select(null);
        if (old is not null)
            await old.CloseAsync();
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

    // ---- 搜索框右侧的“+”菜单 ----

    public bool CanCreateChannel => Session.Permissions.CanCreatePublicChannel || Session.Permissions.CanCreatePrivateChannel;

    /// <summary>发起群聊：至少选择 2 人，打开多人私聊。</summary>
    public void ShowGroupChatPicker()
    {
        UserPickerDialogViewModel picker = null!;
        picker = new UserPickerDialogViewModel(Session, "发起群聊", 2, null, ids =>
        {
            Dialogs.Remove(picker);
            if (ids is not null)
                _ = OpenChatAsync(DmKey.Create(ids, Session.SelfId));
        });
        Dialogs.Add(picker);
    }

    /// <summary>新建话题：记录话题名后打开聊天，第一条消息发出后会话加入消息列表。</summary>
    public void ShowNewTopicDialog()
    {
        NewTopicDialogViewModel dialog = null!;
        dialog = new NewTopicDialogViewModel(Session, result =>
        {
            Dialogs.Remove(dialog);
            if (result is not { } r)
                return;
            Session.Conversations.EnsureEntry(r.Key, r.Name);
            _ = OpenChatAsync(r.Key);
        });
        Dialogs.Add(dialog);
    }

    public void ShowBrowseChannels()
    {
        BrowseChannelsDialogViewModel dialog = null!;
        dialog = new BrowseChannelsDialogViewModel(Session, this, () => Dialogs.Remove(dialog));
        Dialogs.Add(dialog);
    }

    public void ShowCreateChannelDialog()
    {
        CreateChannelDialogViewModel dialog = null!;
        dialog = new CreateChannelDialogViewModel(Session, this, () => Dialogs.Remove(dialog));
        Dialogs.Add(dialog);
    }

    // ---- 个性签名 ----

    [RelayCommand]
    private void BeginEditSignature()
    {
        SignatureDraft = Session.Users.GetStatusText(Session.SelfId) ?? "";
        IsEditingSignature = true;
    }

    /// <summary>提交签名：空文字表示清除。结果由 user_status 事件更新。</summary>
    [RelayCommand]
    private async Task CommitSignatureAsync()
    {
        // 回车和失去焦点都会提交，只处理第一次
        if (!IsEditingSignature)
            return;
        IsEditingSignature = false;
        var text = SignatureDraft.Trim();
        if (text == (Session.Users.GetStatusText(Session.SelfId) ?? ""))
            return;
        try
        {
            await Session.Client.SetStatusAsync(text, Session.Token);
        }
        catch (Exception ex) when (!Session.IsDisposed)
        {
            Log.Warn("修改个性签名失败", ex);
            ShowToast("修改失败");
        }
    }

    [RelayCommand]
    private void CancelEditSignature() => IsEditingSignature = false;

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
