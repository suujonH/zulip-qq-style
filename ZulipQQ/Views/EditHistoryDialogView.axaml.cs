using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ZulipQQ.Controls;
using ZulipQQ.Core;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class EditHistoryDialogView : UserControl
{
    public EditHistoryDialogView()
    {
        InitializeComponent();
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        AddHandler(MessageContent.LinkClickedEvent, OnLinkClicked);
    }

    // 打开后把焦点放进对话框，Esc 才能送到这里
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Dispatcher.UIThread.Post(() => CloseButton.Focus());
    }

    // 对话框在对话框层中，不在聊天视图之下：文件卡片需要的 FileService 在这里提供
    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is EditHistoryDialogViewModel vm)
            MessageContent.SetFiles(this, vm.Session.Files);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || DataContext is not EditHistoryDialogViewModel vm)
            return;
        e.Handled = true;
        Dispatcher.UIThread.Post(() => vm.CloseCommand.Execute(null));
    }

    // 历史内容中的点击：图片打开查看器（只有这一张），提及忽略，链接和文件与聊天中相同
    private void OnLinkClicked(object? sender, MessageLinkEventArgs e)
    {
        if (DataContext is not EditHistoryDialogViewModel vm)
            return;
        e.Handled = true;
        var top = TopLevel.GetTopLevel(this);
        switch (e.Target)
        {
            case MentionTarget:
                break;
            case ImageTarget image:
                ImageViewerWindow.Show(top as Window, vm.Session, [new InlineImage(image.Src, image.Href)], 0);
                break;
            default:
                _ = vm.HandleLinkAsync(e.Target, top);
                break;
        }
    }
}
