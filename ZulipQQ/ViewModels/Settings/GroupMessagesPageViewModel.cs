using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>一个会话（话题或多人私聊）的群消息设置行。用户在下拉框中选择后立即提交。</summary>
public sealed partial class LevelRowViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private bool _updating;

    public LevelRowViewModel(ZulipSession session, ConversationKey key)
    {
        _session = session;
        Key = key;
        Refresh();
    }

    public ConversationKey Key { get; }

    public IReadOnlyList<LevelOption> Options => LevelOption.All;

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    public partial LevelOption? Selected { get; set; }

    /// <summary>从存储重新读取标题与级别（不提交）。</summary>
    public void Refresh()
    {
        _updating = true;
        // 话题显示“备注(话题名)”，便于对照
        Title = Key is TopicKey t ? _session.Conversations.GetTopicTitleWithOriginal(t) : _session.Conversations.GetTitle(Key);
        var level = _session.Conversations.GetLevel(Key);
        Selected = LevelOption.All.First(o => o.Level == level);
        _updating = false;
    }

    partial void OnSelectedChanged(LevelOption? value)
    {
        if (!_updating && value is not null)
            _session.Conversations.SetLevel(Key, value.Level);
    }
}

/// <summary>群消息设置中的一个频道，展开后列出话题。</summary>
public sealed partial class ChannelLevelGroupViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;
    private bool _loaded;

    public ChannelLevelGroupViewModel(ZulipSession session, long streamId, Action<string> toast)
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
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoTopics))]
    public partial bool IsLoading { get; private set; }

    /// <summary>已加载完但频道中没有话题：展开后显示“暂无话题”。</summary>
    public bool HasNoTopics => _loaded && !IsLoading && Topics.Count == 0;

    public ObservableCollection<LevelRowViewModel> Topics { get; } = [];

    public void Refresh()
    {
        if (_session.Channels.Get(StreamId) is not { } channel)
            return;
        Name = _session.Channels.NameWithOriginal(StreamId);
        Fill = ConversationItemViewModel.BrushOf(channel.Color);
        foreach (var row in Topics)
            row.Refresh();
    }

    /// <summary>某个会话的级别变化。</summary>
    public void RefreshRow(ConversationKey key) => Topics.FirstOrDefault(r => r.Key.Equals(key))?.Refresh();

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
            if (!Topics.Select(r => r.Key).ToHashSet().SetEquals(topics.Select(t => new TopicKey(StreamId, t.Name))))
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
            Topics.Add(new LevelRowViewModel(_session, new TopicKey(StreamId, t.Name)));
    }
}

/// <summary>群消息设置：按频道列出话题的四级免打扰，另列出多人私聊。</summary>
public sealed partial class GroupMessagesPageViewModel : SettingsPageViewModel
{
    public GroupMessagesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Build();
        Session.Conversations.EntryChanged += OnEntryChanged;
        Session.Channels.ChannelChanged += OnChannelChanged;
        Session.Channels.TopicsChanged += OnTopicsChanged;
        Session.Channels.Reset += OnReset;
    }

    public ObservableCollection<ChannelLevelGroupViewModel> Channels { get; } = [];

    /// <summary>已归档的频道：单独列出，默认收起，仍可设置（只保存在本机）。</summary>
    public ObservableCollection<ChannelLevelGroupViewModel> ArchivedChannels { get; } = [];

    public bool HasArchivedChannels => ArchivedChannels.Count > 0;

    public string ArchivedTitle => $"已归档的频道（{ArchivedChannels.Count}）";

    [ObservableProperty]
    public partial bool ArchivedExpanded { get; set; }

    public ObservableCollection<LevelRowViewModel> GroupDms { get; } = [];

    public bool HasGroupDms => GroupDms.Count > 0;

    public override void Close()
    {
        Session.Conversations.EntryChanged -= OnEntryChanged;
        Session.Channels.ChannelChanged -= OnChannelChanged;
        Session.Channels.TopicsChanged -= OnTopicsChanged;
        Session.Channels.Reset -= OnReset;
    }

    private void Build()
    {
        Channels.Clear();
        ArchivedChannels.Clear();
        foreach (var channel in Session.Channels.Subscribed())
            ListOf(channel)!.Add(new ChannelLevelGroupViewModel(Session, channel.StreamId, Owner.ShowToast));
        OnPropertyChanged(nameof(HasArchivedChannels));
        OnPropertyChanged(nameof(ArchivedTitle));
        BuildGroupDms();
    }

    // 频道应在的列表：未订阅的不列出
    private ObservableCollection<ChannelLevelGroupViewModel>? ListOf(Channel? channel) =>
        channel is not { IsSubscribed: true } ? null : channel.IsArchived ? ArchivedChannels : Channels;

    private ChannelLevelGroupViewModel? Find(long streamId) =>
        Channels.Concat(ArchivedChannels).FirstOrDefault(c => c.StreamId == streamId);

    private void BuildGroupDms()
    {
        GroupDms.Clear();
        foreach (var entry in Session.Conversations.Entries
                     .Where(e => e.Key is DmKey { IsGroupDm: true })
                     .OrderByDescending(e => e.LastMessageTime))
            GroupDms.Add(new LevelRowViewModel(Session, entry.Key));
        OnPropertyChanged(nameof(HasGroupDms));
    }

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        switch (key)
        {
            case TopicKey t:
                Find(t.StreamId)?.RefreshRow(key);
                break;
            case DmKey { IsGroupDm: true }:
                // 新出现的多人私聊加入列表
                if (GroupDms.FirstOrDefault(r => r.Key.Equals(key)) is { } row)
                    row.Refresh();
                else
                    BuildGroupDms();
                break;
        }
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

    private void OnReset(object? sender, EventArgs e) => Build();
}
