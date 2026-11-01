using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using System.Net;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>开发者模式中请求记录保存的条数的一个选项。</summary>
public sealed record RequestLogLimitOption(int Limit, string Text)
{
    public static readonly IReadOnlyList<RequestLogLimitOption> All =
    [
        new(100, "100 条"),
        new(500, "500 条"),
        new(1000, "1000 条"),
        new(LocalSettings.UnlimitedRequestLogLimit, "无上限"),
    ];

    public override string ToString() => Text;
}

/// <summary>模拟提醒的目标会话（打开设置的账号最近的话题、私聊、多人私聊）。</summary>
public sealed record MockTarget(ConversationKey Key, string Text)
{
    public override string ToString() => Text;
}

/// <summary>
/// 开发者模式：显示设置来源、显示不可用的功能（只模拟操作）、模拟提醒、显示请求（请求记录列表与搜索）。设置都是程序级。
/// </summary>
public sealed partial class DeveloperPageViewModel : SettingsPageViewModel
{
    // 模拟提醒列出的会话数
    private const int MockTargetCount = 30;

    private static RequestLog Records => RequestLog.Instance;

    // 模拟消息的 id：负数，不与服务器的消息重复，也不会被当作未读
    private static long _nextMockId;

    public DeveloperPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Local.Changed += OnLocalChanged;
        Records.Entries.CollectionChanged += OnEntriesChanged;
        Records.EntryCompleted += OnEntryCompleted;
        ApplyFilter();
        _mockSession = AccountHubViewModel.Instance.Active?.Session;
        MockTargets = _mockSession is null ? [] : _mockSession.Conversations.Entries
            .Where(e => e.Key is TopicKey or DmKey { IsSelfDm: false })
            .OrderByDescending(e => e.LastMessageTime)
            .Take(MockTargetCount)
            .Select(e => new MockTarget(e.Key, MockTargetText(e.Key)))
            .ToList();
        MockTargetItem = MockTargets.FirstOrDefault();
    }

    // 模拟提醒发给打开页面时主窗口显示的账号；没有在线的账号时不能模拟
    private readonly ZulipSession? _mockSession;

    private LocalSettings Local => Owner.Local;

    private ZulipSession MockSession => _mockSession!;

    public bool ShowSettingSource
    {
        get => Local.ShowSettingSource;
        set => Local.ShowSettingSource = value;
    }

    public bool ShowUnavailable
    {
        get => Local.ShowUnavailable;
        set => Local.ShowUnavailable = value;
    }

    public bool LogRequests
    {
        get => Local.LogRequests;
        set => Local.LogRequests = value;
    }

    public IReadOnlyList<RequestLogLimitOption> RequestLogLimitOptions => RequestLogLimitOption.All;

    /// <summary>保存的条数，选择即保存。</summary>
    public RequestLogLimitOption? RequestLogLimit
    {
        get => RequestLogLimitOption.All.FirstOrDefault(o => o.Limit == Local.RequestLogLimit);
        set
        {
            if (value is not null && value.Limit != Local.RequestLogLimit)
                Local.RequestLogLimit = value.Limit;
        }
    }

    /// <summary>搜索词：在网址、请求头、请求正文、响应头、响应正文中查找（不区分大小写）。</summary>
    [ObservableProperty]
    public partial string Query { get; set; } = "";

    /// <summary>按时间倒序（最新的在最上方）；点击“时间”列标题切换。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeHeader))]
    public partial bool SortDescending { get; set; } = true;

    /// <summary>“时间”列标题，箭头表示当前的排序方向。</summary>
    public string TimeHeader => SortDescending ? "时间 ↓" : "时间 ↑";

    /// <summary>符合搜索词的记录，按 <see cref="SortDescending"/> 的时间顺序排列。</summary>
    public ObservableCollection<RequestLogEntry> Requests { get; } = [];

    public bool IsEmpty => Requests.Count == 0;

    /// <summary>点击一条记录：由视图在设置窗口右侧打开详情窗口。</summary>
    public event EventHandler<RequestLogEntry>? DetailRequested;

    [RelayCommand]
    private void OpenDetail(RequestLogEntry entry) => DetailRequested?.Invoke(this, entry);

    public IReadOnlyList<MockTarget> MockTargets { get; }

    /// <summary>模拟提醒的会话：模拟消息属于该会话，按它的免打扰级别和通知设置决定是否提醒。</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(MockCommand), nameof(MockReminderCommand))]
    public partial MockTarget? MockTargetItem { get; set; }

    /// <summary>
    /// 模拟收到一条消息（参数为消息的 flags，以逗号分隔；空为普通消息）：只交给提醒服务，按真实规则决定是否闪烁、弹窗、响铃，
    /// 不进入消息、未读和会话列表。
    /// </summary>
    [RelayCommand(CanExecute = nameof(HasMockTarget))]
    private void Mock(string? flags)
    {
        var list = string.IsNullOrEmpty(flags) ? [] : flags.Split(',');
        var text = list switch
        {
            [] => "普通消息",
            ["mentioned"] => "@我",
            ["stream_wildcard_mentioned"] => "@全体成员",
            ["topic_wildcard_mentioned"] => "@话题参与者",
            ["has_alert_word"] => "提醒词",
            _ => flags!,
        };
        var message = CreateMockMessage(MockTargetItem!.Key, text, list);
        if (!Notified(() => MockSession.Notifications.OnNewMessage(message, list)))
            Owner.ShowToast("按当前设置不提醒");
    }

    /// <summary>模拟消息提醒到时间。</summary>
    [RelayCommand(CanExecute = nameof(HasMockTarget))]
    private void MockReminder() => Notified(() => MockSession.Notifications.OnReminder(CreateMockMessage(MockTargetItem!.Key, "消息提醒", [])));

    private bool HasMockTarget() => MockTargetItem is not null && _mockSession is { IsDisposed: false };

    // 执行期间提醒服务是否发出了提醒
    private bool Notified(Action action)
    {
        var notified = false;
        void OnRequested(object? sender, NotificationRequest request) => notified = true;
        MockSession.Notifications.Requested += OnRequested;
        try
        {
            action();
        }
        finally
        {
            MockSession.Notifications.Requested -= OnRequested;
        }
        return notified;
    }

    // 发送者：会话最后一条消息的发送者（不是自己时），否则私聊的对方，否则任意一个其他用户
    private ChatMessage CreateMockMessage(ConversationKey key, string text, IReadOnlyList<string> flags)
    {
        var self = MockSession.SelfId;
        var last = MockSession.Conversations.Entries.FirstOrDefault(e => e.Key.Equals(key))?.LastSenderId ?? 0;
        var senderId = last != 0 && last != self ? last
            : key is DmKey { IsSelfDm: false } dm ? dm.OtherUserIds[0]
            : MockSession.Users.ActiveHumans(excludeSelf: true).FirstOrDefault()?.Id ?? self;
        var sender = MockSession.Users.Get(senderId);
        var topic = key as TopicKey;
        return new ChatMessage
        {
            Id = Interlocked.Decrement(ref _nextMockId),
            Key = key,
            SenderId = senderId,
            SenderName = MockSession.Users.NameOf(senderId),
            SenderEmail = sender?.Email ?? "",
            AvatarUrl = sender?.AvatarUrl,
            ContentHtml = $"<p>【测试】{WebUtility.HtmlEncode(text)}</p>",
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Flags = [.. flags],
            Topic = topic?.Topic ?? "",
            StreamId = topic?.StreamId,
        };
    }

    private string MockTargetText(ConversationKey key) => key switch
    {
        TopicKey => "话题：" + MockSession.Conversations.GetDisplayTitle(key),
        DmKey { IsGroupDm: true } => "多人私聊：" + MockSession.Conversations.GetTitle(key),
        _ => "私聊：" + MockSession.Conversations.GetTitle(key),
    };

    [RelayCommand]
    private void Clear() => Records.Clear();

    [RelayCommand]
    private void ToggleSort() => SortDescending = !SortDescending;

    public override void Close()
    {
        Local.Changed -= OnLocalChanged;
        Records.Entries.CollectionChanged -= OnEntriesChanged;
        Records.EntryCompleted -= OnEntryCompleted;
    }

    partial void OnQueryChanged(string value) => ApplyFilter();

    partial void OnSortDescendingChanged(bool value) => ApplyFilter();

    // 记录按发出的先后保存，倒序时反转
    private void ApplyFilter()
    {
        var q = Query.Trim();
        var matched = q.Length == 0 ? Records.Entries.ToList() : Records.Entries.Where(e => e.Matches(q)).ToList();
        if (SortDescending)
            matched.Reverse();
        CollectionSync.SyncTo(Requests, matched);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e) => ApplyFilter();

    // 收到响应后响应头、正文才能被搜索到
    private void OnEntryCompleted(object? sender, RequestLogEntry entry)
    {
        if (Query.Trim().Length > 0)
            ApplyFilter();
    }

    private void OnLocalChanged(object? sender, string key)
    {
        switch (key)
        {
            case LocalSettings.ShowUnavailableKey:
                OnPropertyChanged(nameof(ShowUnavailable));
                break;
            case LocalSettings.LogRequestsKey:
                OnPropertyChanged(nameof(LogRequests));
                break;
            case LocalSettings.RequestLogLimitKey:
                OnPropertyChanged(nameof(RequestLogLimit));
                break;
        }
    }
}
