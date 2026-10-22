using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using ZulipQQ.Core;
using ZulipQQ.Services;
using ZulipQQ.Zulip.Dtos;

namespace ZulipQQ.ViewModels.Settings;

/// <summary>一个会话（话题或多人私聊）的群消息设置行。用户在下拉框中选择后立即提交。</summary>
public sealed partial class LevelRowViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;
    private bool _updating;

    public LevelRowViewModel(ZulipSession session, ConversationKey key, Action<string> toast)
    {
        _session = session;
        _toast = toast;
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
        Title = _session.Conversations.GetTitle(Key);
        var level = _session.Conversations.GetLevel(Key);
        Selected = LevelOption.All.First(o => o.Level == level);
        _updating = false;
    }

    partial void OnSelectedChanged(LevelOption? value)
    {
        if (!_updating && value is not null)
            _ = ApplyAsync(value.Level);
    }

    private async Task ApplyAsync(DndLevel level)
    {
        try
        {
            await _session.Conversations.SetLevelAsync(Key, level);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"设置群消息级别失败：{Key.ToStorageString()}", ex);
            _toast("设置失败");
            Refresh();
        }
    }
}

/// <summary>群消息设置中的一个频道：频道静音开关，展开后列出话题。</summary>
public sealed partial class ChannelLevelGroupViewModel : ObservableObject
{
    private readonly ZulipSession _session;
    private readonly Action<string> _toast;
    private bool _updating;
    private bool _loaded;

    public ChannelLevelGroupViewModel(ZulipSession session, long streamId, Action<string> toast)
    {
        _session = session;
        _toast = toast;
        StreamId = streamId;
        Refresh();
    }

    public long StreamId { get; }

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    [ObservableProperty]
    public partial IBrush? Fill { get; private set; }

    [ObservableProperty]
    public partial bool IsMuted { get; set; }

    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    public ObservableCollection<LevelRowViewModel> Topics { get; } = [];

    public void Refresh()
    {
        if (_session.Channels.Get(StreamId) is not { } channel)
            return;
        _updating = true;
        Name = channel.Name;
        Fill = ConversationItemViewModel.BrushOf(channel.Color);
        IsMuted = channel.IsMuted;
        _updating = false;
        foreach (var row in Topics)
            row.Refresh();
    }

    /// <summary>某个会话的级别变化。</summary>
    public void RefreshRow(ConversationKey key) => Topics.FirstOrDefault(r => r.Key.Equals(key))?.Refresh();

    partial void OnIsExpandedChanged(bool value)
    {
        if (value && !_loaded)
            _ = LoadTopicsAsync();
    }

    partial void OnIsMutedChanged(bool value)
    {
        if (!_updating)
            _ = SetMutedAsync(value);
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
            Topics.Clear();
            foreach (var t in topics)
            {
                var key = new TopicKey(StreamId, t.Name);
                Topics.Add(new LevelRowViewModel(_session, key, _toast));
            }
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

    // 结果以 subscription 事件为准；失败时恢复开关
    private async Task SetMutedAsync(bool muted)
    {
        try
        {
            await _session.Client.UpdateSubscriptionPropertiesAsync([new SubscriptionPropertyChange(StreamId, "is_muted", muted)], _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"设置频道静音失败：{StreamId}", ex);
            _toast("设置失败");
            Refresh();
        }
    }
}

/// <summary>群消息设置：按频道列出话题的四级免打扰，另列出多人私聊。</summary>
public sealed class GroupMessagesPageViewModel : SettingsPageViewModel
{
    public GroupMessagesPageViewModel(SettingsViewModel owner) : base(owner)
    {
        Build();
        Session.Conversations.EntryChanged += OnEntryChanged;
        Session.Channels.ChannelChanged += OnChannelChanged;
        Session.Channels.Reset += OnReset;
    }

    public ObservableCollection<ChannelLevelGroupViewModel> Channels { get; } = [];

    public ObservableCollection<LevelRowViewModel> GroupDms { get; } = [];

    public bool HasGroupDms => GroupDms.Count > 0;

    public override void Close()
    {
        Session.Conversations.EntryChanged -= OnEntryChanged;
        Session.Channels.ChannelChanged -= OnChannelChanged;
        Session.Channels.Reset -= OnReset;
    }

    private void Build()
    {
        Channels.Clear();
        foreach (var channel in Session.Channels.Subscribed())
            Channels.Add(new ChannelLevelGroupViewModel(Session, channel.StreamId, Owner.ShowToast));

        GroupDms.Clear();
        foreach (var entry in Session.Conversations.Entries
                     .Where(e => e.Key is DmKey { IsGroupDm: true })
                     .OrderByDescending(e => e.LastMessageTime))
            GroupDms.Add(new LevelRowViewModel(Session, entry.Key, Owner.ShowToast));
        OnPropertyChanged(nameof(HasGroupDms));
    }

    private void OnEntryChanged(object? sender, ConversationKey key)
    {
        switch (key)
        {
            case TopicKey t:
                Channels.FirstOrDefault(c => c.StreamId == t.StreamId)?.RefreshRow(key);
                break;
            case DmKey:
                GroupDms.FirstOrDefault(r => r.Key.Equals(key))?.Refresh();
                break;
        }
    }

    private void OnChannelChanged(object? sender, long streamId)
    {
        var group = Channels.FirstOrDefault(c => c.StreamId == streamId);
        var subscribed = Session.Channels.Get(streamId) is { IsSubscribed: true };
        if (group is null == subscribed)
            Build();
        else
            group?.Refresh();
    }

    private void OnReset(object? sender, EventArgs e) => Build();
}
