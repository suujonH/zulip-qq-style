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
        MemberList.PointerReleased += OnMemberPointerReleased;
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

    // 点击成员：弹出名片
    private void OnMemberPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_vm is not { } vm || e.InitialPressMouseButton != MouseButton.Left)
            return;
        for (var v = e.Source as Visual; v is not null && v != MemberList; v = v.GetVisualParent())
        {
            if (v is ListBoxItem { DataContext: ChatMemberItem member } item)
            {
                ChatView.ShowCard(close => vm.CreateUserCard(member.UserId, close), item, PlacementMode.LeftEdgeAlignedTop);
                return;
            }
        }
    }
}
