using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class ChatMorePanelView : UserControl
{
    private ChatMorePanelViewModel? _vm;

    public ChatMorePanelView()
    {
        InitializeComponent();
        ShareButton.Click += (_, _) =>
        {
            if (_vm is { } vm)
                _ = ConversationMenus.CopyAsync(ShareButton, vm.Link, vm.Host);
        };
        MoveButton.Click += (_, _) => ShowMoveMenu();
        HistoryButton.Click += (_, _) =>
        {
            if (_vm is { } vm)
                HistoryWindow.Show(TopLevel.GetTopLevel(this) as Window, vm.Main, vm.Key);
        };
        FilesButton.Click += (_, _) =>
        {
            if (_vm is { } vm)
                FilesWindow.ShowSingle(TopLevel.GetTopLevel(this) as Window, vm.Main, FilesView.Conversation, vm.Key);
        };
        foreach (var list in new Control[] { PreviewMemberList, FullMemberList })
        {
            list.DoubleTapped += OnMemberDoubleTapped;
            list.ContextRequested += OnMemberContextRequested;
        }
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
        _vm?.Detach();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _vm?.Detach();
        _vm = DataContext as ChatMorePanelViewModel;
        if (TopLevel.GetTopLevel(this) is not null)
            _vm?.Attach();
    }

    /// <summary>“移动群到其他分组”：列出可以移入的频道。</summary>
    private void ShowMoveMenu()
    {
        if (_vm is not { } vm)
            return;
        var menu = new ContextMenu();
        foreach (var channel in vm.MoveTargets())
            menu.Items.Add(ConversationMenus.Item($"#{channel.Name}", () => vm.MoveTo(channel)));
        if (menu.Items.Count > 0)
            menu.Open(MoveButton);
    }

    // 成员：左键单击没有反应，双击发消息，右键菜单
    private void OnMemberDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { } vm && ChatView.FindMember(e.Source as Visual, (Visual)sender!) is { DataContext: ChatMemberItem member })
            vm.Chat.OpenDirectChat(member.UserId);
    }

    private void OnMemberContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || ChatView.FindMember(e.Source as Visual, (Visual)sender!) is not { DataContext: ChatMemberItem member } row)
            return;
        e.Handled = true;
        ChatView.ShowUserMenu(vm.Chat, member.UserId, row, PlacementMode.LeftEdgeAlignedTop);
    }
}
