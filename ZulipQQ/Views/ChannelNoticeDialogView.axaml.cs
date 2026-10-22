using System;
using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class ChannelNoticeDialogView : UserControl
{
    public ChannelNoticeDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
    }

    // Esc：编辑中取消编辑，否则关闭
    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not ChannelNoticeDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() =>
        {
            if (vm.IsEditing)
                vm.CancelEditCommand.Execute(null);
            else
                vm.CloseCommand.Execute(null);
        });
    }

    // 公告中的网页链接用浏览器打开
    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        e.Handled = true;
        if (e.Target is not UrlTarget url || !Uri.TryCreate(url.Href, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https"))
            return;
        try
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Warn($"打开链接失败：{uri}", ex);
        }
    }
}
