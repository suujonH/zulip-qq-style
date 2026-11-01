using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using ZulipQQ.Services;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class UserCardView : UserControl
{
    private UserCardViewModel? _vm;

    // 复制邮箱后监听剪贴板，名片关闭（离开可视树）时结束
    private ClipboardWatcher? _clipboardWatcher;

    public UserCardView()
    {
        InitializeComponent();
    }

    // 显示期间（在可视树上）随数据变化刷新
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _vm?.Attach();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopWatchingClipboard();
        _vm?.Detach();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        StopWatchingClipboard();
        _vm?.Detach();
        _vm = DataContext as UserCardViewModel;
        if (TopLevel.GetTopLevel(this) is not null)
            _vm?.Attach();
    }

    private async void OnCopyEmailClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not { Email.Length: > 0 } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        try
        {
            await clipboard.SetTextAsync(vm.Email);
        }
        catch (Exception ex)
        {
            Log.Warn("复制邮箱失败", ex);
            return;
        }
        if (_vm != vm || TopLevel.GetTopLevel(this) is null)
            return;
        vm.IsEmailCopied = true;
        _clipboardWatcher ??= ClipboardWatcher.Start(OnClipboardChanged);
    }

    // 剪贴板被改为邮箱以外的内容（包括图片等非文字）时恢复复制图标；读取失败时也恢复，不显示可能不对的 √
    private async void OnClipboardChanged()
    {
        if (_vm is not { IsEmailCopied: true } vm || TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            return;
        string? text;
        try
        {
            text = await clipboard.TryGetTextAsync();
        }
        catch (Exception ex)
        {
            Log.Warn("读取剪贴板失败", ex);
            text = null;
        }
        if (_vm != vm || text == vm.Email)
            return;
        StopWatchingClipboard();
    }

    private void StopWatchingClipboard()
    {
        _clipboardWatcher?.Dispose();
        _clipboardWatcher = null;
        if (_vm is not null)
            _vm.IsEmailCopied = false;
    }
}
