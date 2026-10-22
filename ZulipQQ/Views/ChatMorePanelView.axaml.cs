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
                _ = ConversationMenus.CopyAsync(ShareButton, vm.Link, vm.Main);
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
        PreviewMemberList.PointerReleased += OnMemberPointerReleased;
        FullMemberList.PointerReleased += OnMemberPointerReleased;
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

    // 点击成员：弹出名片
    private void OnMemberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is not { } vm || e.InitialPressMouseButton != MouseButton.Left)
            return;
        for (var v = e.Source as Visual; v is not null && v != sender; v = v.GetVisualParent())
        {
            if (v is Control { DataContext: ChatMemberItem member } c)
            {
                ChatView.ShowCard(close => vm.CreateUserCard(member.UserId, close), c, PlacementMode.LeftEdgeAlignedTop);
                return;
            }
        }
    }
}
