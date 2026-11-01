using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ZulipQQ.ViewModels;

namespace ZulipQQ.Views;

public partial class TopicSidebarView : UserControl
{
    private TopicSidebarViewModel? _vm;

    public TopicSidebarView()
    {
        InitializeComponent();
        MemberList.DoubleTapped += OnMemberDoubleTapped;
        MemberList.ContextRequested += OnMemberContextRequested;
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
        if (_vm is not null)
        {
            _vm.Detach();
            _vm.PropertyChanged -= OnVmPropertyChanged;
        }
        _vm = DataContext as TopicSidebarViewModel;
        if (_vm is null)
            return;
        _vm.PropertyChanged += OnVmPropertyChanged;
        if (TopLevel.GetTopLevel(this) is not null)
            _vm.Attach();
    }

    // 打开搜索时把焦点放进搜索框
    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TopicSidebarViewModel.IsSearching) && _vm is { IsSearching: true })
            Dispatcher.UIThread.Post(() => SearchBox.Focus());
    }

    // 成员：左键单击没有反应，双击发消息，右键菜单
    private void OnMemberDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (_vm is { } vm && ChatView.FindMember(e.Source as Visual, MemberList) is { DataContext: ChatMemberItem member })
            vm.Chat.OpenDirectChat(member.UserId);
    }

    private void OnMemberContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (_vm is not { } vm || ChatView.FindMember(e.Source as Visual, MemberList) is not { DataContext: ChatMemberItem member } row)
            return;
        e.Handled = true;
        ChatView.ShowUserMenu(vm.Chat, member.UserId, row, PlacementMode.LeftEdgeAlignedTop);
    }
}
