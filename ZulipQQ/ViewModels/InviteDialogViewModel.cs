using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ZulipQQ.Services;
using ZulipQQ.Zulip;

namespace ZulipQQ.ViewModels;

/// <summary>
/// “邀请成员”对话框：输入邮箱（逗号、分号或换行分隔），选择受邀者加入的频道，以普通成员身份发送邀请邮件。
/// 成功后关闭并提示；失败时保留对话框并提示服务器的错误。
/// </summary>
public partial class InviteDialogViewModel : ViewModelBase
{
    private readonly ZulipSession _session;
    private readonly MainViewModel _main;
    private readonly Action _onClosed;

    public InviteDialogViewModel(ZulipSession session, MainViewModel main, Action onClosed)
    {
        _session = session;
        _main = main;
        _onClosed = onClosed;
        Channels = session.Channels.Subscribed()
            .Where(c => !c.IsArchived)
            .Select(c => new InviteChannelOption(c.StreamId, c.Name, c.InviteOnly))
            .ToList();
    }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial string Emails { get; set; } = "";

    public IReadOnlyList<InviteChannelOption> Channels { get; }

    public bool HasChannels => Channels.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConfirmCommand))]
    public partial bool IsBusy { get; private set; }

    private bool CanConfirm => !IsBusy && ParseEmails(Emails).Count > 0;

    /// <summary>拆分输入的邮箱：逗号（含中文逗号）、分号、空白都可作分隔；去掉空项，忽略大小写去重。</summary>
    public static IReadOnlyList<string> ParseEmails(string text) =>
        text.Split([',', '，', ';', '；', ' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    [RelayCommand(CanExecute = nameof(CanConfirm))]
    private async Task ConfirmAsync()
    {
        var emails = ParseEmails(Emails);
        if (emails.FirstOrDefault(e => !e.Contains('@')) is { } invalid)
        {
            _main.ShowToast($"邮箱格式不正确：{invalid}");
            return;
        }

        IsBusy = true;
        try
        {
            var streamIds = Channels.Where(c => c.IsChecked).Select(c => c.StreamId).ToList();
            await _session.Client.SendInvitesAsync(string.Join(",", emails), streamIds, _session.Token);
            _onClosed();
            _main.ShowToast("邀请已发送");
        }
        catch (Exception ex) when (!_session.IsDisposed)
        {
            Log.Warn("发送邀请失败", ex);
            _main.ShowToast(ex is ZulipApiException { Message.Length: > 0 } api ? $"邀请失败：{api.Message}" : "邀请失败");
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void Cancel() => _onClosed();
}

/// <summary>邀请对话框中的一个频道选项。</summary>
public partial class InviteChannelOption(long streamId, string name, bool isPrivate) : ViewModelBase
{
    public long StreamId { get; } = streamId;
    public string Name { get; } = name;
    public bool IsPrivate { get; } = isPrivate;

    [ObservableProperty]
    public partial bool IsChecked { get; set; }
}
