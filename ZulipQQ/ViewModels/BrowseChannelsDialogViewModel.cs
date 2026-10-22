using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Core;
using ZulipQQ.Models;
using ZulipQQ.Services;

namespace ZulipQQ.ViewModels;

/// <summary>浏览频道中的一行。</summary>
public partial class ChannelBrowseItem(long streamId) : ObservableObject
{
    public long StreamId { get; } = streamId;

    [ObservableProperty]
    public partial string Name { get; private set; } = "";

    /// <summary>描述的第一行。</summary>
    [ObservableProperty]
    public partial string Description { get; private set; } = "";

    [ObservableProperty]
    public partial string SubscriberText { get; private set; } = "";

    [ObservableProperty]
    public partial bool IsPrivate { get; private set; }

    [ObservableProperty]
    public partial bool IsSubscribed { get; private set; }

    /// <summary>加入或退出的请求进行中，按钮禁用。</summary>
    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void Update(Channel c)
    {
        Name = c.Name;
        Description = c.Description.Split('\n', 2)[0].Trim();
        SubscriberText = $"{c.SubscriberCount.ToString(CultureInfo.InvariantCulture)} 人";
        IsPrivate = c.InviteOnly;
        IsSubscribed = c.IsSubscribed;
    }
}

/// <summary>
/// “加入频道”对话框：列出可见、未归档的频道，可加入或退出。
/// 未订阅且没有权限加入的频道不显示。订阅状态由 subscription 事件更新。
/// </summary>
public partial class BrowseChannelsDialogViewModel : ViewModelBase
{
    private static readonly StringComparer NameComparer =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), CompareOptions.None);

    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly Action _onClosed;
    private readonly Dictionary<long, ChannelBrowseItem> _items = [];

    public BrowseChannelsDialogViewModel(ZulipSession session, MainViewModel main, Action onClosed)
    {
        _session = session;
        _main = main;
        _onClosed = onClosed;
        session.Channels.ChannelChanged += OnChannelChanged;
        session.Channels.Reset += OnChannelsReset;
        Refresh();
    }

    [ObservableProperty]
    public partial string Query { get; set; } = "";

    public ObservableCollection<ChannelBrowseItem> Items { get; } = [];

    public bool IsEmpty => Items.Count == 0;

    partial void OnQueryChanged(string value) => Refresh();

    private void OnChannelChanged(object? sender, long id) => Refresh();

    private void OnChannelsReset(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        var p = _session.Permissions;
        var q = Query.Trim();
        var desired = new List<ChannelBrowseItem>();
        foreach (var c in _session.Channels.All
                     .Where(c => !c.IsArchived && (c.IsSubscribed || p.CanSubscribe(c, c.InviteOnly, c.CanSubscribeGroup)))
                     .Where(c => q.Length == 0 || c.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                 || c.Description.Contains(q, StringComparison.OrdinalIgnoreCase))
                     .OrderBy(c => c.Name, NameComparer))
        {
            if (!_items.TryGetValue(c.StreamId, out var item))
                _items[c.StreamId] = item = new ChannelBrowseItem(c.StreamId);
            item.Update(c);
            desired.Add(item);
        }
        CollectionSync.SyncTo(Items, desired);
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private async Task JoinAsync(ChannelBrowseItem item)
    {
        if (item.IsBusy)
            return;
        item.IsBusy = true;
        try
        {
            await _session.Client.SubscribeAsync([item.Name], null, _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"加入频道失败：{item.StreamId}", ex);
            _main.ShowToast(ex is Zulip.ZulipApiException api ? $"加入失败：{api.Message}" : "加入失败");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task LeaveAsync(ChannelBrowseItem item)
    {
        if (item.IsBusy || !await _main.ConfirmAsync($"确定要退订频道 #{item.Name} 吗？", "退订"))
            return;
        item.IsBusy = true;
        try
        {
            await _session.Client.UnsubscribeAsync([item.Name], null, _session.Token);
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"退订频道失败：{item.StreamId}", ex);
            _main.ShowToast("退订失败");
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        _session.Channels.ChannelChanged -= OnChannelChanged;
        _session.Channels.Reset -= OnChannelsReset;
        _onClosed();
    }
}
