using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>邮件合并等待时间的一个选项（Zulip 的 email_notifications_batching_period_seconds）。</summary>
public sealed record BatchingOption(int Value, string Text)
{
    public static readonly IReadOnlyList<BatchingOption> All =
    [
        new(120, "2 分钟"),
        new(300, "5 分钟"),
        new(600, "10 分钟"),
        new(1800, "30 分钟"),
        new(3600, "1 小时"),
        new(21600, "6 小时"),
        new(86400, "1 天"),
        new(604800, "1 周"),
    ];

    public override string ToString() => Text;
}

/// <summary>邮件标题中是否包含组织名称（Zulip 的 realm_name_in_email_notifications_policy）。</summary>
public sealed record RealmNameOption(int Value, string Text)
{
    public static readonly IReadOnlyList<RealmNameOption> All =
    [
        new(1, "自动"),
        new(2, "总是"),
        new(3, "从不"),
    ];

    public override string ToString() => Text;
}

/// <summary>
/// 自动关注 / 自动取消静音话题的条件（Zulip 的 automatically_follow_topics_policy、
/// automatically_unmute_topics_in_muted_streams_policy）。
/// </summary>
public sealed record AutoPolicyOption(int Value, string Text)
{
    public static readonly IReadOnlyList<AutoPolicyOption> All =
    [
        new(1, "我参与的话题"),
        new(2, "我发过消息的话题"),
        new(3, "我发起的话题"),
        new(4, "从不"),
    ];

    public override string ToString() => Text;
}

/// <summary>
/// 一路外部通知（手机推送或邮件）会因哪些消息发送：行首为手机或邮件图标，之后每种触发情况（@我、@全体、所有消息）一个图标；
/// 都不触发时只有灰色的行首图标。
/// </summary>
public sealed record PushEffectLine(string IconKey, string Label, PushTriggers Triggers)
{
    public bool Mention => Triggers.HasFlag(PushTriggers.Mention);

    public bool Wildcard => Triggers.HasFlag(PushTriggers.Wildcard);

    public bool AllMessages => Triggers.HasFlag(PushTriggers.AllMessages);

    public bool IsNone => Triggers == PushTriggers.None;

    /// <summary>“手机推送：@我、@全体”。</summary>
    public string ToolTip => $"{Label}：{(IsNone ? "不发送" : PushEffect.Describe(Triggers))}";
}

/// <summary>
/// 强化推送中的一个话题：Zulip 的 visibility_policy（右侧三个图标：跟随频道、静音 / 取消静音、关注，同时只有一个高亮）
/// 与按服务器规则算出的推送效果。点击后立即提交。
/// </summary>
public sealed partial class PushTopicRowViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;

    /// <param name="topicName">服务器上的原始话题名。</param>
    public PushTopicRowViewModel(ZulipSession session, long streamId, string topicName, Action<string> toast)
    {
        _session = session;
        _toast = toast;
        StreamId = streamId;
        TopicName = topicName;
        Refresh();
    }

    public long StreamId { get; }

    public string TopicName { get; }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    /// <summary>话题的 visibility_policy，见 <see cref="TopicVisibility"/>。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsInherit), nameof(IsMuteSet), nameof(IsUnmuteOption), nameof(MuteIconKey), nameof(MuteTip),
        nameof(IsFollowed))]
    public partial int Policy { get; private set; }

    /// <summary>频道是否静音：决定中间图标是“静音”还是“取消静音”。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUnmuteOption), nameof(MuteIconKey), nameof(MuteTip))]
    public partial bool ChannelMuted { get; private set; }

    public bool IsInherit => Policy == TopicVisibility.Inherit;

    /// <summary>中间图标高亮：话题设为静音或取消静音。</summary>
    public bool IsMuteSet => Policy is TopicVisibility.Muted or TopicVisibility.Unmuted;

    /// <summary>
    /// 中间图标表示“取消静音”：话题已设为取消静音，或频道静音（此时只有取消静音有意义）；否则表示“静音”。
    /// </summary>
    public bool IsUnmuteOption => Policy == TopicVisibility.Unmuted || (Policy != TopicVisibility.Muted && ChannelMuted);

    public string MuteIconKey => IsUnmuteOption ? "Icon.Notification" : "Icon.NotificationOff";

    public string MuteTip => IsUnmuteOption ? "取消静音（频道静音时照常通知）" : "静音";

    public bool IsFollowed => Policy == TopicVisibility.Followed;

    /// <summary>推送效果：手机推送（服务器未开启时没有）、邮件各一行。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PushEffectLine> Effects { get; private set; } = [];

    /// <summary>已归档频道的话题不能修改（服务器拒绝），图标不可点击。</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    /// <summary>重新读取话题状态与推送效果（不提交）。</summary>
    public void Refresh()
    {
        Title = _session.Conversations.GetTopicTitleWithOriginal(new TopicKey(StreamId, TopicName));
        ApplyLocal(_session.Channels.TopicPolicy(StreamId, TopicName));
    }

    [RelayCommand]
    private void FollowChannel() => Set(TopicVisibility.Inherit);

    // 再次点击已高亮的图标回到跟随频道
    [RelayCommand]
    private void ToggleMute() =>
        Set(IsMuteSet ? TopicVisibility.Inherit : ChannelMuted ? TopicVisibility.Unmuted : TopicVisibility.Muted);

    [RelayCommand]
    private void ToggleFollow() => Set(IsFollowed ? TopicVisibility.Inherit : TopicVisibility.Followed);

    private void ApplyLocal(int policy)
    {
        var channel = _session.Channels.Get(StreamId);
        ChannelMuted = channel?.IsMuted == true;
        CanEdit = channel is { IsArchived: false };
        Policy = policy;
        Effects = channel is not null ? EnhancedPushPageViewModel.EffectLines(_session, channel, policy) : [];
    }

    // 立即显示新状态，失败时恢复
    private void Set(int policy)
    {
        if (policy == Policy || !CanEdit)
            return;
        ApplyLocal(policy);
        _ = ApplyAsync(policy);
    }

    private async Task ApplyAsync(int policy)
    {
        try
        {
            await _session.Client.SetTopicVisibilityAsync(StreamId, TopicName, policy, _session.Token);
            if (_session.IsDisposed)
                return;
            // 立即在本地生效，不等 user_topic 事件
            _session.Channels.OnUserTopic(new UserTopicDto { StreamId = StreamId, TopicName = TopicName, VisibilityPolicy = policy });
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"设置话题推送失败：{StreamId} {TopicName}", ex);
            _toast("设置失败");
            Refresh();
        }
    }
}

/// <summary>强化推送中的一个频道：频道静音与推送、邮件、@全体设置，展开后列出话题。</summary>
public sealed partial class PushChannelGroupViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;
    private bool _updating;
    private bool _loaded;

    public PushChannelGroupViewModel(ZulipSession session, long streamId, Action<string> toast)
    {
        _session = session;
        _toast = toast;
        StreamId = streamId;
        Refresh();
        Topics.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasNoTopics));
    }

    public long StreamId { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial IBrush? Fill { get; private set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    /// <summary>已归档的频道不能修改订阅设置（服务器返回 Invalid channel ID），图标不可点击。</summary>
    [ObservableProperty]
    public partial bool CanEdit { get; private set; }

    /// <summary>服务器是否开启了手机推送；未开启时隐藏“手机推送所有消息”。</summary>
    public bool PushAvailable => _session.Shows(_session.Realm.PushNotificationsEnabled);

    /// <summary>手机推送所有消息；频道未设置时显示全局设置。</summary>
    [ObservableProperty]
    public partial bool PushAll { get; set; }

    /// <summary>邮件发送所有消息；频道未设置时显示全局设置。</summary>
    [ObservableProperty]
    public partial bool EmailAll { get; set; }

    /// <summary>@全体成员时推送和发邮件；频道未设置时显示全局设置。</summary>
    [ObservableProperty]
    public partial bool Wildcard { get; set; }

    /// <summary>没有单独设置的话题（包括新话题）的推送效果：手机推送（服务器未开启时没有）、邮件各一行。</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PushEffectLine> Effects { get; private set; } = [];

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTopics))]
    public partial bool IsLoading { get; private set; }

    /// <summary>已加载完但频道中没有话题：展开后显示“暂无话题”。</summary>
    public bool HasNoTopics => _loaded && !IsLoading && Topics.Count == 0;

    public ObservableCollection<PushTopicRowViewModel> Topics { get; } = [];

    public void Refresh()
    {
        if (_session.Channels.Get(StreamId) is not { } channel)
            return;
        var z = _session.Settings.Zulip;
        _updating = true;
        Name = _session.Channels.NameWithOriginal(StreamId);
        Fill = ConversationItemViewModel.BrushOf(channel.Color);
        CanEdit = !channel.IsArchived;
        IsMuted = channel.IsMuted;
        PushAll = channel.PushNotifications ?? z.EnableStreamPushNotifications;
        EmailAll = channel.EmailNotifications ?? z.EnableStreamEmailNotifications;
        Wildcard = channel.WildcardMentionsNotify ?? z.WildcardMentionsNotify;
        Effects = EnhancedPushPageViewModel.EffectLines(_session, channel, TopicVisibility.Inherit);
        _updating = false;
        OnPropertyChanged(nameof(PushAvailable));
        foreach (var row in Topics)
            row.Refresh();
    }

    /// <summary>
    /// 话题列表变化（新话题，或改名、移动后缓存被丢弃）：已加载的按最新列表重建，话题行不会留着旧的话题名。
    /// </summary>
    public void OnTopicsChanged()
    {
        if (!_loaded || IsLoading)
            return;
        if (_session.Channels.TryGetTopics(StreamId) is { } topics)
        {
            // 只是已有话题收到新消息时不重建，避免打开中的下拉框被关闭
            // 按原始话题名比较：只改了大小写也要重建，提交时需要服务器上的话题名
            if (!Topics.Select(r => r.TopicName).ToHashSet(StringComparer.Ordinal).SetEquals(topics.Select(t => t.Name)))
                FillTopics(topics);
            return;
        }
        _loaded = false;
        Topics.Clear();
        if (IsExpanded)
            _ = LoadTopicsAsync();
    }

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
            _ = LoadTopicsAsync();
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_updating)
            _ = SetPropertyAsync("is_muted", value);
    }

    partial void OnPushAllChanged(bool value)
    {
        if (_updating)
            return;
        // 开发者模式下服务器未开启手机推送时只模拟，图标恢复原状
        if (_session.Mocks(_session.Realm.PushNotificationsEnabled, _toast))
            Dispatcher.UIThread.Post(Refresh);
        else
            _ = SetPropertyAsync("push_notifications", value);
    }

    partial void OnEmailAllChanged(bool value)
    {
        if (!_updating)
            _ = SetPropertyAsync("email_notifications", value);
    }

    partial void OnWildcardChanged(bool value)
    {
        if (!_updating)
            _ = SetPropertyAsync("wildcard_mentions_notify", value);
    }

    private async Task LoadTopicsAsync()
    {
        _loaded = true;
        IsLoading = true;
        try
        {
            var topics = await _session.Channels.GetTopicsAsync(StreamId);
            if (_session.IsDisposed)
                return;
            FillTopics(topics);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"加载话题失败：{StreamId}", ex);
            _loaded = false;
            _toast("加载话题失败");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void FillTopics(IReadOnlyList<TopicInfo> topics)
    {
        Topics.Clear();
        foreach (var t in topics)
            Topics.Add(new PushTopicRowViewModel(_session, StreamId, t.Name, _toast));
    }

    // 结果以 subscription 事件为准；失败时恢复开关
    private async Task SetPropertyAsync(string property, bool value)
    {
        try
        {
            await _session.Client.UpdateSubscriptionPropertiesAsync([new SubscriptionPropertyChange(StreamId, property, value)], _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"修改频道推送设置失败：{StreamId} {property}", ex);
            _toast("设置失败");
            Refresh();
        }
    }
}

/// <summary>
/// 强化推送：原样提供 Zulip 服务器的手机推送、消息邮件、@全体与自动关注设置（账号相关的邮件在「Zulip 账号」中），
/// 以及各频道、话题的推送相关设置。所有修改立即提交到 Zulip。
/// </summary>
public sealed partial class EnhancedPushPageViewModel : SettingsPageViewModel
{
    public EnhancedPushPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Build();
        Session.Settings.Changed += OnSettingsChanged;
        Session.Channels.ChannelChanged += OnChannelChanged;
        Session.Channels.TopicsChanged += OnTopicsChanged;
        Session.Channels.Reset += OnReset;
        Session.PermissionsChanged += OnRealmChanged;
        // 话题备注变化时更新话题名
        Session.Conversations.EntryChanged += OnEntryChanged;
    }

    private UserSettingsDto Z => Session.Settings.Zulip;

    /// <summary>服务器是否开启了手机推送；未开启时隐藏手机推送相关设置（开发者模式下显示，修改时只模拟）。</summary>
    public bool PushAvailable => Session.Shows(Session.Realm.PushNotificationsEnabled);

    // 手机推送
    public bool EnableOfflinePushNotifications
    {
        get => Z.EnableOfflinePushNotifications;
        set => SetPush("enable_offline_push_notifications", Z.EnableOfflinePushNotifications, value);
    }

    public bool EnableOnlinePushNotifications
    {
        get => Z.EnableOnlinePushNotifications;
        set => SetPush("enable_online_push_notifications", Z.EnableOnlinePushNotifications, value);
    }

    public bool EnableStreamPushNotifications
    {
        get => Z.EnableStreamPushNotifications;
        set => SetPush("enable_stream_push_notifications", Z.EnableStreamPushNotifications, value);
    }

    public bool EnableFollowedTopicPushNotifications
    {
        get => Z.EnableFollowedTopicPushNotifications;
        set => SetPush("enable_followed_topic_push_notifications", Z.EnableFollowedTopicPushNotifications, value);
    }

    // 邮件
    public bool EnableOfflineEmailNotifications
    {
        get => Z.EnableOfflineEmailNotifications;
        set => Set("enable_offline_email_notifications", Z.EnableOfflineEmailNotifications, value);
    }

    public bool EnableStreamEmailNotifications
    {
        get => Z.EnableStreamEmailNotifications;
        set => Set("enable_stream_email_notifications", Z.EnableStreamEmailNotifications, value);
    }

    public bool EnableFollowedTopicEmailNotifications
    {
        get => Z.EnableFollowedTopicEmailNotifications;
        set => Set("enable_followed_topic_email_notifications", Z.EnableFollowedTopicEmailNotifications, value);
    }

    public bool MessageContentInEmailNotifications
    {
        get => Z.MessageContentInEmailNotifications;
        set => Set("message_content_in_email_notifications", Z.MessageContentInEmailNotifications, value);
    }

    public bool EnableDigestEmails
    {
        get => Z.EnableDigestEmails;
        set => Set("enable_digest_emails", Z.EnableDigestEmails, value);
    }

    /// <summary>固定选项；当前值不在其中时（在网页端或通过 API 设置）在末尾加上“自定义”。</summary>
    public IReadOnlyList<BatchingOption> BatchingOptions
    {
        get
        {
            var value = Z.EmailNotificationsBatchingPeriodSeconds;
            return BatchingOption.All.Any(o => o.Value == value)
                ? BatchingOption.All
                : [.. BatchingOption.All, new BatchingOption(value, $"自定义（{value} 秒）")];
        }
    }

    public BatchingOption? Batching
    {
        get => BatchingOptions.FirstOrDefault(o => o.Value == Z.EmailNotificationsBatchingPeriodSeconds);
        set
        {
            if (value is not null && value.Value != Z.EmailNotificationsBatchingPeriodSeconds)
                SetZulip("email_notifications_batching_period_seconds", value.Value);
        }
    }

    public IReadOnlyList<RealmNameOption> RealmNameOptions => RealmNameOption.All;

    public RealmNameOption? RealmName
    {
        get => RealmNameOption.All.FirstOrDefault(o => o.Value == Z.RealmNameInEmailNotificationsPolicy);
        set
        {
            if (value is not null && value.Value != Z.RealmNameInEmailNotificationsPolicy)
                SetZulip("realm_name_in_email_notifications_policy", value.Value);
        }
    }

    // @全体与自动关注
    public bool WildcardMentionsNotify
    {
        get => Z.WildcardMentionsNotify;
        set => Set("wildcard_mentions_notify", Z.WildcardMentionsNotify, value);
    }

    public bool EnableFollowedTopicWildcardMentionsNotify
    {
        get => Z.EnableFollowedTopicWildcardMentionsNotify;
        set => Set("enable_followed_topic_wildcard_mentions_notify", Z.EnableFollowedTopicWildcardMentionsNotify, value);
    }

    public bool AutomaticallyFollowTopicsWhereMentioned
    {
        get => Z.AutomaticallyFollowTopicsWhereMentioned;
        set => Set("automatically_follow_topics_where_mentioned", Z.AutomaticallyFollowTopicsWhereMentioned, value);
    }

    public IReadOnlyList<AutoPolicyOption> AutoPolicyOptions => AutoPolicyOption.All;

    public AutoPolicyOption? AutoFollowPolicy
    {
        get => AutoPolicyOption.All.FirstOrDefault(o => o.Value == Z.AutomaticallyFollowTopicsPolicy);
        set
        {
            if (value is not null && value.Value != Z.AutomaticallyFollowTopicsPolicy)
                SetZulip("automatically_follow_topics_policy", value.Value);
        }
    }

    public AutoPolicyOption? AutoUnmutePolicy
    {
        get => AutoPolicyOption.All.FirstOrDefault(o => o.Value == Z.AutomaticallyUnmuteTopicsInMutedStreamsPolicy);
        set
        {
            if (value is not null && value.Value != Z.AutomaticallyUnmuteTopicsInMutedStreamsPolicy)
                SetZulip("automatically_unmute_topics_in_muted_streams_policy", value.Value);
        }
    }

    public ObservableCollection<PushChannelGroupViewModel> Channels { get; } = [];

    /// <summary>已归档的频道：单独列出，默认收起，不能修改。</summary>
    public ObservableCollection<PushChannelGroupViewModel> ArchivedChannels { get; } = [];

    public bool HasArchivedChannels => ArchivedChannels.Count > 0;

    public string ArchivedTitle => $"已归档的频道（{ArchivedChannels.Count}）";

    [ObservableProperty]
    public partial bool ArchivedExpanded { get; set; }

    /// <summary>话题的推送效果：第一行手机推送，第二行邮件；服务器未开启手机推送时只有邮件一行。</summary>
    public static IReadOnlyList<PushEffectLine> EffectLines(ZulipSession session, Channel channel, int policy)
    {
        var z = session.Settings.Zulip;
        var email = new PushEffectLine("Icon.Mail", "邮件",
            PushEffect.Evaluate(EmailRulesOf(z), channel.IsMuted, channel.EmailNotifications, channel.WildcardMentionsNotify, policy));
        if (!session.Shows(session.Realm.PushNotificationsEnabled))
            return [email];
        var phone = new PushEffectLine("Icon.Mobile", "手机推送",
            PushEffect.Evaluate(PushRulesOf(z), channel.IsMuted, channel.PushNotifications, channel.WildcardMentionsNotify, policy));
        return [phone, email];
    }

    public override void Close()
    {
        Session.Settings.Changed -= OnSettingsChanged;
        Session.Channels.ChannelChanged -= OnChannelChanged;
        Session.Channels.TopicsChanged -= OnTopicsChanged;
        Session.Channels.Reset -= OnReset;
        Session.PermissionsChanged -= OnRealmChanged;
        Session.Conversations.EntryChanged -= OnEntryChanged;
    }

    private static PushRules PushRulesOf(UserSettingsDto z) => new(z.EnableOfflinePushNotifications, z.EnableStreamPushNotifications,
        z.EnableFollowedTopicPushNotifications, z.WildcardMentionsNotify, z.EnableFollowedTopicWildcardMentionsNotify);

    private static PushRules EmailRulesOf(UserSettingsDto z) => new(z.EnableOfflineEmailNotifications, z.EnableStreamEmailNotifications,
        z.EnableFollowedTopicEmailNotifications, z.WildcardMentionsNotify, z.EnableFollowedTopicWildcardMentionsNotify);

    private void Build()
    {
        Channels.Clear();
        ArchivedChannels.Clear();
        foreach (var channel in Session.Channels.Subscribed())
            ListOf(channel)!.Add(new PushChannelGroupViewModel(Session, channel.StreamId, Owner.ShowToast));
        OnPropertyChanged(nameof(HasArchivedChannels));
        OnPropertyChanged(nameof(ArchivedTitle));
    }

    // 频道应在的列表：未订阅的不列出
    private ObservableCollection<PushChannelGroupViewModel>? ListOf(Channel? channel) =>
        channel is not { IsSubscribed: true } ? null : channel.IsArchived ? ArchivedChannels : Channels;

    private PushChannelGroupViewModel? Find(long streamId) =>
        Channels.Concat(ArchivedChannels).FirstOrDefault(c => c.StreamId == streamId);

    private void Set(string name, bool current, bool value)
    {
        if (value != current)
            SetZulip(name, value);
    }

    // 手机推送的设置：开发者模式下服务器未开启手机推送时只模拟，开关恢复原状
    private void SetPush(string name, bool current, bool value)
    {
        if (value == current)
            return;
        if (Session.Mocks(Session.Realm.PushNotificationsEnabled, Owner.ShowToast))
            Dispatcher.UIThread.Post(() => OnPropertyChanged(string.Empty));
        else
            SetZulip(name, value);
    }

    // 全局设置会影响所有频道与话题的推送效果
    private void OnSettingsChanged(object? sender, string name) => RefreshAll();

    // 组织设置变化（realm 事件）：服务器可能开启或关闭了手机推送
    private void OnRealmChanged(object? sender, EventArgs e) => RefreshAll();

    private void RefreshAll()
    {
        OnPropertyChanged(string.Empty);
        foreach (var group in Channels.Concat(ArchivedChannels))
            group.Refresh();
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        // 订阅、退订、归档或取消归档时重建
        var group = Find(streamId);
        var list = group is null ? null : Channels.Contains(group) ? Channels : ArchivedChannels;
        if (list != ListOf(Session.Channels.Get(streamId)))
            Build();
        else
            group?.Refresh();
    }

    private void OnTopicsChanged(object? sender, long streamId) => Find(streamId)?.OnTopicsChanged();

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        if (key is TopicKey t)
            Find(t.StreamId)?.Topics.FirstOrDefault(r => key.Equals(new TopicKey(r.StreamId, r.TopicName)))?.Refresh();
    }

    private void OnReset(object? sender, EventArgs e) => Build();
}
