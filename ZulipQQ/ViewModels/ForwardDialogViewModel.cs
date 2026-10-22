using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>转发对话框中的一个会话。</summary>
public partial class ForwardTargetItem(ConversationKey key, string title, string? avatarUrl, string avatarText, IBrush? avatarFill,
    string? channelName = null) : ObservableObject
{
    public ConversationKey Key { get; } = key;

    public string Title { get; } = title;

    /// <summary>话题所属频道（“#名称”），区分不同频道中的同名话题；私聊为 null。</summary>
    public string? ChannelText { get; } = channelName is null ? null : "#" + channelName;

    public string? AvatarUrl { get; } = avatarUrl;

    public string AvatarText { get; } = avatarText;

    public IBrush? AvatarFill { get; } = avatarFill;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}

/// <summary>
/// 转发对话框：左侧为最近会话（消息列表的前 50 个）或搜索结果，勾选目标（最多 9 个）；
/// 右侧为已选会话、被转发内容的预览和留言。发送时先取得要发送的内容（消息原文），
/// 再对每个目标依次发送，留言作为单独的一条消息。Zulip 没有原生转发，转发即以自己的身份重新发送原文。
/// </summary>
public partial class ForwardDialogViewModel : ViewModelBase
{
    public const int MaxTargets = 9;
    private const int MaxResults = 50;

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly Func<Task<IReadOnlyList<string>>> _build;
    private readonly Action _onClosed;
    private readonly Dictionary<ConversationKey, ForwardTargetItem> _items = [];
    private bool _closed;

    // 超过上限时撤销勾选，期间不再处理勾选变化
    private bool _reverting;

    private ForwardDialogViewModel(MainViewModel main, string preview, Func<Task<IReadOnlyList<string>>> build, Action onClosed)
    {
        _session = main.Session;
        _main = main;
        Preview = preview;
        _build = build;
        _onClosed = onClosed;
        NoteMaxLength = _session.Realm.MaxMessageLength > 0 ? _session.Realm.MaxMessageLength : 10000;
        ApplyFilter();
    }

    /// <summary>
    /// 在主窗口的对话框层中打开。<paramref name="build"/> 在点击发送后调用，返回要发送给每个目标的消息（按顺序）。
    /// </summary>
    public static void Show(MainViewModel main, string preview, Func<Task<IReadOnlyList<string>>> build)
    {
        ForwardDialogViewModel dialog = null!;
        dialog = new ForwardDialogViewModel(main, preview, build, () => main.Dialogs.Remove(dialog));
        main.Dialogs.Add(dialog);
    }

    /// <summary>被转发内容的预览。</summary>
    public string Preview { get; }

    public int NoteMaxLength { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsRecent))]
    public partial string Query { get; set; } = "";

    /// <summary>没有搜索词时显示最近会话。</summary>
    public bool ShowsRecent => Query.Trim().Length == 0;

    /// <summary>留言，作为单独的一条消息发送。</summary>
    [ObservableProperty]
    public partial string Note { get; set; } = "";

    public ObservableCollection<ForwardTargetItem> Results { get; } = [];

    /// <summary>已选择的会话，按选择顺序。</summary>
    public ObservableCollection<ForwardTargetItem> Selected { get; } = [];

    public string SelectedText => $"已选择 {Selected.Count}/{MaxTargets} 个会话";

    public bool IsEmpty => Results.Count == 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanSend => Selected.Count > 0 && !IsBusy;

    partial void OnQueryChanged(string value) => ApplyFilter();

    /// <summary>没有搜索词时为最近会话；有搜索词时在全部会话和全部用户中按名称、备注、邮箱查找。</summary>
    private void ApplyFilter()
    {
        var q = Query.Trim();
        var entries = _session.Conversations.Entries
            .Where(e => e.Key is not HelperKey && CanSendTo(e.Key))
            .OrderByDescending(e => e.IsPinned)
            .ThenByDescending(e => e.PinnedAt ?? 0)
            .ThenByDescending(e => e.LastMessageTime);

        List<ConversationKey> keys;
        if (q.Length == 0)
        {
            keys = entries.Where(e => e.InList).Select(e => e.Key).Take(MaxResults).ToList();
        }
        else
        {
            keys = entries.Where(e => Matches(_session.Conversations.GetTitle(e.Key), q)).Select(e => e.Key).ToList();
            var users = _session.Users.ActiveHumans(excludeSelf: true)
                .Where(u => Matches(_session.Contacts.DisplayName(u.Id), q) || Matches(u.FullName, q) || Matches(u.Email, q))
                .Select(u => (ConversationKey)DmKey.Create([u.Id], _session.SelfId));
            keys = keys.Concat(users).Distinct().Take(MaxResults).ToList();
        }
        CollectionSync.SyncTo(Results, keys.Select(ItemFor).ToList());
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static bool Matches(string text, string q) => text.Contains(q, StringComparison.OrdinalIgnoreCase);

    // 只能转发到可以发言的会话：已订阅、未归档且有发言权限的频道；私聊对象都是活跃用户
    private bool CanSendTo(ConversationKey key) => key switch
    {
        TopicKey t => _session.Channels.Get(t.StreamId) is { IsSubscribed: true, IsArchived: false } channel
                      && _session.Permissions.CanSendToChannel(channel),
        DmKey d => d.OtherUserIds.All(id => _session.Users.Get(id) is { IsActive: true }),
        _ => false,
    };

    private ForwardTargetItem ItemFor(ConversationKey key)
    {
        if (_items.TryGetValue(key, out var item))
            return item;
        var title = _session.Conversations.GetTitle(key);
        item = key switch
        {
            TopicKey t => new ForwardTargetItem(key, title, null, title,
                ConversationItemViewModel.BrushOf(_session.Channels.Get(t.StreamId)?.Color), _session.Channels.Get(t.StreamId)?.Name),
            DmKey { IsSelfDm: true } => new ForwardTargetItem(key, title, _session.Users.Get(_session.SelfId)?.AvatarUrl, title, null),
            DmKey { OtherUserIds.Length: 1 } d => new ForwardTargetItem(key, title, _session.Users.Get(d.OtherUserIds[0])?.AvatarUrl, title, null),
            _ => new ForwardTargetItem(key, title, null, "群", ConversationItemViewModel.BrushOf(MessagesPageViewModel.GroupDmAvatarColor)),
        };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ForwardTargetItem.IsChecked))
                OnItemChecked(item);
        };
        _items[key] = item;
        return item;
    }

    private void OnItemChecked(ForwardTargetItem item)
    {
        if (_reverting)
            return;
        if (item.IsChecked && !Selected.Contains(item))
        {
            if (Selected.Count >= MaxTargets)
            {
                _reverting = true;
                item.IsChecked = false;
                _reverting = false;
                _main.ShowToast($"最多选择{MaxTargets}个会话");
                return;
            }
            Selected.Add(item);
        }
        else if (!item.IsChecked)
        {
            Selected.Remove(item);
        }
        OnPropertyChanged(nameof(SelectedText));
        SendCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void Remove(ForwardTargetItem item) => item.IsChecked = false;

    /// <summary>
    /// 发送：先取得原文（失败时提示并保持对话框打开），然后关闭对话框，对每个目标依次发送原文和留言。
    /// 每条都等服务器接受后再发下一条，保证顺序。
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        IsBusy = true;
        IReadOnlyList<string> contents;
        try
        {
            contents = await _build();
        }
        catch (Exception ex)
        {
            if (_session.IsDisposed)
                return;
            Log.Warn("获取转发内容失败", ex);
            _main.ShowToast("转发失败");
            IsBusy = false;
            return;
        }
        if (_closed || _session.IsDisposed)
            return;

        var targets = Selected.Select(i => i.Key).ToList();
        var note = Note.Trim();
        Close();

        var ok = true;
        foreach (var key in targets)
        {
            foreach (var content in contents)
                ok &= await _session.Chat.SendAsync(key, content);
            if (note.Length > 0)
                ok &= await _session.Chat.SendAsync(key, note);
            if (_session.IsDisposed)
                return;
        }
        _main.ShowToast(ok ? "已转发" : "部分消息转发失败");
    }

    [RelayCommand]
    private void Cancel() => Close();

    private void Close()
    {
        if (_closed)
            return;
        _closed = true;
        _onClosed();
    }
}
