using System;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// 群公告对话框：显示频道描述（服务器渲染的 HTML）。频道管理员可以编辑，保存后由频道更新事件刷新。
/// </summary>
public partial class ChannelNoticeDialogViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly long _streamId;
    private readonly Action _onClosed;

    private ChannelNoticeDialogViewModel(MainViewModel main, long streamId, Action onClosed)
    {
        _session = main.Session;
        _main = main;
        _streamId = streamId;
        _onClosed = onClosed;
        Refresh();
    }

    /// <summary>在主窗口的对话框层中打开。</summary>
    public static void Show(MainViewModel main, long streamId)
    {
        ChannelNoticeDialogViewModel dialog = null!;
        dialog = new ChannelNoticeDialogViewModel(main, streamId, () =>
        {
            main.Dialogs.Remove(dialog);
            dialog.Detach();
        });
        dialog.Attach();
        main.Dialogs.Add(dialog);
    }

    [ObservableProperty]
    public partial string Title { get; private set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial string Html { get; private set; } = "";

    public bool IsEmpty => Html.Length == 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditButton))]
    public partial bool CanEdit { get; private set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEditButton))]
    public partial bool IsEditing { get; private set; }

    public bool ShowEditButton => CanEdit && !IsEditing;

    [ObservableProperty]
    public partial string Draft { get; set; } = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    public partial bool IsBusy { get; private set; }

    private void Attach()
    {
        _session.Channels.ChannelChanged += OnChannelChanged;
        _session.Resynced += OnResynced;
    }

    private void Detach()
    {
        _session.Channels.ChannelChanged -= OnChannelChanged;
        _session.Resynced -= OnResynced;
    }

    // 重新 register 后频道整体重建，不会逐个触发 ChannelChanged
    private void OnResynced(object? sender, EventArgs e) => Refresh();

    private void OnChannelChanged(object? sender, long streamId)
    {
        if (streamId == _streamId)
            Refresh();
    }

    private void Refresh()
    {
        var channel = _session.Channels.Get(_streamId);
        Title = channel is null ? "群公告" : $"群公告 - #{channel.Name}";
        Html = channel?.RenderedDescription.Trim() ?? "";
        CanEdit = channel is { IsArchived: false } && _session.Permissions.CanAdministerChannel(channel);
        if (!CanEdit)
            IsEditing = false;
    }

    [RelayCommand]
    private void BeginEdit()
    {
        Draft = _session.Channels.Get(_streamId)?.Description ?? "";
        IsEditing = true;
    }

    [RelayCommand]
    private void CancelEdit() => IsEditing = false;

    private bool CanSave => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync()
    {
        IsBusy = true;
        try
        {
            var args = new FormBuilder();
            args.Add("description", Draft.Trim());
            await _session.Client.UpdateChannelAsync(_streamId, args, _session.Token);
            IsEditing = false;
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn($"修改群公告失败：{_streamId}", ex);
            _main.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"保存失败：{api.Message}" : "保存失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Close() => _onClosed();
}
